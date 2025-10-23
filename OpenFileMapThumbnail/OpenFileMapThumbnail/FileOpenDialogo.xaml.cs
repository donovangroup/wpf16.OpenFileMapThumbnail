using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OpenFileMapThumbnail
{
    public partial class FileOpenDialogo : Window
    {
        public string SelectedFile { get; private set; }
        private readonly string[] _imageExts = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tiff", ".webp" };

        public FileOpenDialogo(string initialFolder = null)
        {
            InitializeComponent();
            var start = initialFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            FolderBox.Text = start;
            LoadTiles(start);
        }

        private void LoadTiles(string folder)
        {
            TilePanel.Children.Clear();

            if (!Directory.Exists(folder))
            {
                CountText.Text = "Folder not found.";
                return;
            }

            try
            {
                // Show subfolders as clickable tiles too
                foreach (var dir in Directory.GetDirectories(folder))
                {
                    TilePanel.Children.Add(CreateTile(dir, isFolder: true));
                }

                foreach (var file in Directory.GetFiles(folder))
                {
                    string ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
                    if (_imageExts.Contains(ext))
                        TilePanel.Children.Add(CreateTile(file, isFolder: false));
                }

                CountText.Text = $"{TilePanel.Children.Count} items";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Error loading folder",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private Border CreateTile(string path, bool isFolder)
        {
            var grid = new Grid { Width = 220, Height = 124 };
            grid.Margin = new Thickness(8);

            ImageSource thumb = isFolder ? GetFolderIcon() : GetThumbnail(path, 220, 124);
            var img = new Image
            {
                Source = thumb,
                Stretch = Stretch.UniformToFill,
                ClipToBounds = true
            };
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
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = path
            };

            border.MouseEnter += (s, e) => border.BorderBrush = Brushes.Cyan;
            border.MouseLeave += (s, e) => border.BorderBrush = Brushes.Transparent;
            border.MouseLeftButtonUp += (s, e) =>
            {
                if (isFolder)
                {
                    FolderBox.Text = path;
                    LoadTiles(path);
                }
                else
                {
                    SelectedFile = path;
                    DialogResult = true;
                }
            };

            return border;
        }

        private static ImageSource GetThumbnail(string path, int width, int height)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.DecodePixelWidth = width;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return GetFolderIcon(); // fallback
            }
        }

        private static ImageSource GetFolderIcon()
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(80, 80, 90)), null, new Rect(0, 0, 128, 72));
                dc.DrawText(
                    new FormattedText("📁", System.Globalization.CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, new Typeface("Segoe UI Emoji"), 32, Brushes.Gold, 1.0),
                    new Point(46, 20));
            }
            var bmp = new RenderTargetBitmap(128, 72, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(dv);
            bmp.Freeze();
            return bmp;
        }

        private void Up_Click(object sender, RoutedEventArgs e)
        {
            var dir = FolderBox.Text;
            var parent = Directory.GetParent(dir);
            if (parent != null)
            {
                FolderBox.Text = parent.FullName;
                LoadTiles(parent.FullName);
            }
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
    }
}
