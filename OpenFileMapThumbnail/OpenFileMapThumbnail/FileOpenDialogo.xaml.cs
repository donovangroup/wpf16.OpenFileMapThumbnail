using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace OpenFileMapThumbnail
{
    public partial class FileOpenDialogo : Window
    {
        public string SelectedFile { get; private set; }

        // Set this to your actual GSHHS shapefile path (.shp)
        private readonly string _gshhsPath = @"C:\Users\user\Documents\GSHHS_shp\l\GSHHS_l_L1.shp";

        // Supported image formats for inline preview
        private readonly string[] _imageExts = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tiff", ".webp" };

        // Async rendering infra
        private CancellationTokenSource _cts;
        private readonly SemaphoreSlim _thumbWorkers =
            new SemaphoreSlim(Math.Max(2, Environment.ProcessorCount / 2));

        // Tile presentation knobs
        private const int TileWidth = 220;
        private const int TileHeight = 124;
        private const double ZoomRadiusNm = 300; // default zoom for initial preview
        private const double PadRatio = 1.15;    // buffer to reduce coastline clipping
        private const double CrossOpacity = 0.85;

        // Simple per-tile state
        private sealed class TileState
        {
            public string Path { get; set; }
            public bool IsFolder { get; set; }
            public Image ImageControl { get; set; }
            public ImageSource DefaultPreview { get; set; }
            public ImageSource HoverPreview { get; set; }
            public bool HoverRenderStarted { get; set; }

            // NEW: if true, skip all hover swaps (used when no platforms)
            public bool DisableHover { get; set; }
        }

        public FileOpenDialogo(string initialFolder = null)
        {
            InitializeComponent();

            var start = initialFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            FolderBox.Text = start;
            LoadTiles(start);
        }

        // -----------------------
        // Folder Loading / Tiles
        // -----------------------

        private void LoadTiles(string folder)
        {
            // cancel any in-flight work
            if (_cts != null) _cts.Cancel();
            _cts = new CancellationTokenSource();

            TilePanel.Children.Clear();

            if (!Directory.Exists(folder))
            {
                CountText.Text = "Folder not found.";
                return;
            }

            try
            {
                // Collect entries (folders first, then files we care about)
                var entries = new List<(string path, bool isFolder)>();

                foreach (var dir in SafeGetDirectories(folder))
                    entries.Add((dir, true));

                foreach (var file in SafeGetFiles(folder))
                {
                    string ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
                    if (_imageExts.Contains(ext) || ext == ".exercise")
                        entries.Add((file, false));
                }

                CountText.Text = string.Format("{0} items", entries.Count);

                // Create placeholders immediately; render each tile preview in the background
                foreach (var item in entries)
                {
                    var tuple = CreatePlaceholderTile(item.path, item.isFolder);
                    var border = tuple.border;
                    var state = tuple.state;

                    TilePanel.Children.Add(border);

                    // fire-and-forget background render per tile
                    var _ = RenderTileAsync(state, _cts.Token);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Error loading folder",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private IEnumerable<string> SafeGetDirectories(string folder)
        {
            try { return Directory.GetDirectories(folder); }
            catch { return new string[0]; }
        }

        private IEnumerable<string> SafeGetFiles(string folder)
        {
            try { return Directory.GetFiles(folder); }
            catch { return new string[0]; }
        }

        private (Border border, Image image, TileState state) CreatePlaceholderTile(string path, bool isFolder)
        {
            var grid = new Grid { Width = TileWidth, Height = TileHeight, Margin = new Thickness(8) };

            // Fast solid placeholder (so dialog appears instantly)
            var phDv = new DrawingVisual();
            using (var dc = phDv.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(48, 51, 60)), null, new Rect(0, 0, TileWidth, TileHeight));
            }
            var phBmp = new RenderTargetBitmap(TileWidth, TileHeight, 96, 96, PixelFormats.Pbgra32);
            phBmp.Render(phDv);
            phBmp.Freeze();

            var img = new Image
            {
                Source = phBmp,
                Stretch = Stretch.UniformToFill,
                ClipToBounds = true,
                Opacity = 0.85 // makes the fade-in a touch more noticeable
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            grid.Children.Add(img);

            var caption = new TextBlock
            {
                Text = System.IO.Path.GetFileName(path),
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)),
                VerticalAlignment = VerticalAlignment.Bottom,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Padding = new Thickness(4, 2, 4, 2)
            };
            grid.Children.Add(caption);

            var border = new Border
            {
                Child = grid,
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand
            };

            // Tile state cache
            var state = new TileState
            {
                Path = path,
                IsFolder = isFolder,
                ImageControl = img
            };
            border.Tag = state;

            border.MouseEnter += Border_MouseEnter;
            border.MouseLeave += Border_MouseLeave;
            border.MouseLeftButtonUp += Border_MouseLeftButtonUp;
            border.MouseEnter += (s, e) => ((Border)s).BorderBrush = Brushes.Cyan;
            border.MouseLeave += (s, e) => ((Border)s).BorderBrush = Brushes.Transparent;

            return (border, img, state);
        }

        // -----------------------
        // Background rendering
        // -----------------------

        private async Task RenderTileAsync(TileState state, CancellationToken ct)
        {
            try
            {
                await _thumbWorkers.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (ct.IsCancellationRequested) return;

                    ImageSource result;

                    if (state.IsFolder)
                    {
                        result = GetFolderIcon();
                    }
                    else
                    {
                        string ext = System.IO.Path.GetExtension(state.Path).ToLowerInvariant();

                        if (ext == ".exercise")
                        {
                            // Build markers from the exercise
                            var centerMarkers = new List<(double lon, double lat)>();
                            double? cLon = null, cLat = null;

                            double centerLon, centerLat;
                            if (MapTileRenderer.TryReadExerciseCenter(state.Path, out centerLon, out centerLat))
                            {
                                centerMarkers.Add((centerLon, centerLat));
                                cLon = centerLon;
                                cLat = centerLat;
                            }

                            var launchMarkers = MapTileRenderer.TryReadLaunchPlatformPositions(state.Path);
                            var otherMarkers = MapTileRenderer.TryReadOtherPlatformPositions(state.Path);

                            // Render zoomed-in map with slight padding to keep coasts smooth
                            result = MapTileRenderer.RenderWorldMap(
                                _gshhsPath,
                                pixelWidth: TileWidth,
                                pixelHeight: TileHeight,
                                markers: centerMarkers,                // white cross(es)
                                launchMarkers: launchMarkers,          // yellow diamond(s)
                                otherPlatformMarkers: otherMarkers,    // blue dot(s)
                                centerLon: cLon,
                                centerLat: cLat,
                                radiusNm: ZoomRadiusNm,
                                padRatio: PadRatio,
                                crossOpacity: CrossOpacity
                            );
                        }
                        else if (_imageExts.Contains(ext))
                        {
                            result = LoadThumbnail(state.Path, TileWidth, TileHeight);
                        }
                        else
                        {
                            // Unhandled file type → keep placeholder
                            return;
                        }
                    }

                    if (ct.IsCancellationRequested) return;

                    // Swap into UI and cache default
                    await Dispatcher.InvokeAsync(new Action(() =>
                    {
                        if (ct.IsCancellationRequested) return;
                        state.ImageControl.Source = result;
                        state.ImageControl.Opacity = 1.0;
                        state.DefaultPreview = result;
                        FadeIn(state.ImageControl, 180);
                    }));
                }
                finally
                {
                    _thumbWorkers.Release();
                }
            }
            catch (OperationCanceledException)
            {
                // Navigated to another folder; ignore
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(string.Format("RenderTileAsync error for {0}: {1}", state.Path, ex.Message));
            }
        }

        private static ImageSource LoadThumbnail(string path, int width, int height)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.DecodePixelWidth = width;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze(); // IMPORTANT for cross-thread usage
                return bmp;
            }
            catch
            {
                return GetFolderIcon();
            }
        }

        private static ImageSource GetFolderIcon()
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(60, 65, 75)), null, new Rect(0, 0, 128, 72));
                dc.DrawText(
                    new FormattedText(
                        "📁",
                        System.Globalization.CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        new Typeface("Segoe UI Emoji"),
                        32,
                        Brushes.Gold,
                        1.0),
                    new Point(46, 20));
            }

            var bmp = new RenderTargetBitmap(128, 72, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(dv);
            bmp.Freeze();
            return bmp;
        }

        // -----------------------
        // Hover zoom (UI)
        // -----------------------

        private async void Border_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            Border b;
            if (!(sender is Border))
                return;
            b = (Border)sender;

            TileState st;
            if (!(b.Tag is TileState))
                return;
            st = (TileState)b.Tag;

            if (st.IsFolder) return;
            if (st.DisableHover) return; // <- skip hover behavior for no-platform tiles

            string ext = System.IO.Path.GetExtension(st.Path).ToLowerInvariant();
            if (ext != ".exercise") return;

            // If we don't have a hover preview yet, render it in the background (only once)
            if (!st.HoverRenderStarted && st.HoverPreview == null)
            {
                st.HoverRenderStarted = true;

                try
                {
                    await _thumbWorkers.WaitAsync(_cts.Token);
                    try
                    {
                        // Gather platform markers first
                        var launch = MapTileRenderer.TryReadLaunchPlatformPositions(st.Path);
                        var other = MapTileRenderer.TryReadOtherPlatformPositions(st.Path);

                        // If there are no platforms at all, disable hover and bail
                        bool noPlatforms =
                            (launch == null || launch.Count == 0) &&
                            (other == null || other.Count == 0);

                        if (noPlatforms)
                        {
                            st.DisableHover = true;   // future hovers do nothing
                            st.HoverPreview = null;   // ensure no swap attempt
                            return;
                        }

                        // Compute auto-fit view over ALL platforms
                        var auto = MapTileRenderer.ComputeAutoView(launch, other, 60, 30);

                        // Build markers
                        var centerMarkers = new List<(double lon, double lat)>();
                        double cLon, cLat;
                        if (MapTileRenderer.TryReadExerciseCenter(st.Path, out cLon, out cLat))
                            centerMarkers.Add((cLon, cLat));

                        var hoverImg = MapTileRenderer.RenderWorldMap(
                            _gshhsPath,
                            TileWidth, TileHeight,
                            markers: centerMarkers,
                            launchMarkers: launch,
                            otherPlatformMarkers: other,
                            centerLon: auto.centerLon,
                            centerLat: auto.centerLat,
                            radiusNm: auto.radiusNm,
                            padRatio: 1.10,
                            crossOpacity: 0.9);

                        st.HoverPreview = hoverImg; // Frozen inside renderer
                    }
                    finally
                    {
                        _thumbWorkers.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(string.Format("Hover render error for {0}: {1}", st.Path, ex.Message));
                    return;
                }
            }

            // If hover image is available and it's different, swap it in with a fade
            if (st.HoverPreview != null && !SameSource(st.ImageControl, st.HoverPreview))
            {
                st.ImageControl.Source = st.HoverPreview;
                FadeIn(st.ImageControl, 160);
            }
        }

        private void Border_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            Border b;
            if (!(sender is Border))
                return;
            b = (Border)sender;

            TileState st;
            if (!(b.Tag is TileState))
                return;
            st = (TileState)b.Tag;

            if (st.DefaultPreview != null && !SameSource(st.ImageControl, st.DefaultPreview))
            {
                st.ImageControl.Source = st.DefaultPreview;
                FadeIn(st.ImageControl, 160);
            }
        }

        private void Border_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            Border b;
            if (!(sender is Border))
                return;
            b = (Border)sender;

            TileState st;
            if (!(b.Tag is TileState))
                return;
            st = (TileState)b.Tag;

            if (Directory.Exists(st.Path))
            {
                FolderBox.Text = st.Path;
                LoadTiles(st.Path);
            }
            else
            {
                SelectedFile = st.Path;
                DialogResult = true;
            }
        }

        // -----------------------
        // UI Buttons
        // -----------------------

        private void Up_Click(object sender, RoutedEventArgs e)
        {
            var dir = FolderBox.Text;
            try
            {
                var parent = Directory.GetParent(dir);
                if (parent != null)
                {
                    FolderBox.Text = parent.FullName;
                    LoadTiles(parent.FullName);
                }
            }
            catch { /* ignore */ }
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            LoadTiles(FolderBox.Text);
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(SelectedFile))
                DialogResult = true;
            else
                MessageBox.Show("Please select a file.");
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            try { if (_cts != null) _cts.Cancel(); } catch { }
            _thumbWorkers.Dispose();
        }

        // -----------------------
        // Small helpers
        // -----------------------

        private static void FadeIn(Image img, double durationMs = 180)
        {
            img.Opacity = 0.0;
            var anim = new DoubleAnimation
            {
                From = 0.0,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(durationMs),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };
            anim.Completed += delegate { img.Opacity = 1.0; };
            img.BeginAnimation(UIElement.OpacityProperty, anim, HandoffBehavior.SnapshotAndReplace);
        }

        private static bool SameSource(Image img, ImageSource src)
        {
            // Reference equality is sufficient; RenderWorldMap returns frozen bitmaps.
            return object.ReferenceEquals(img.Source, src);
        }
    }
}
 