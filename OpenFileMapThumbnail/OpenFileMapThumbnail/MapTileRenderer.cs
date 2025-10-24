using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

// Avoid Point clash with NetTopologySuite
using WpfPoint = System.Windows.Point;

namespace OpenFileMapThumbnail
{
    public static class MapTileRenderer
    {
        /// <summary>
        /// Render a land map from the shapefile.
        ///  - markers: white cross(es) (e.g., exercise center)
        ///  - launchMarkers: yellow diamond(s) (drawn last, on top)
        ///  - otherPlatformMarkers: blue small dot(s) (drawn first)
        ///  - centerLon/centerLat + radiusNm: zoom window in nautical miles (1° ≈ 60 nm)
        ///  - padRatio: expands the deg window slightly to reduce hard coastline clipping
        ///  - crossOpacity: opacity of the white cross (0..1)
        /// </summary>
        public static ImageSource RenderWorldMap(
            string shpPath,
            int pixelWidth,
            int pixelHeight,
            IEnumerable<(double lon, double lat)> markers = null,
            IEnumerable<(double lon, double lat)> launchMarkers = null,
            IEnumerable<(double lon, double lat)> otherPlatformMarkers = null,
            Color? landColor = null,
            Color? waterColor = null,
            Color? strokeColor = null,
            double? centerLon = null,
            double? centerLat = null,
            double radiusNm = 500,
            double padRatio = 1.0,
            double crossOpacity = 0.85)
        {
            if (string.IsNullOrWhiteSpace(shpPath) || !File.Exists(shpPath))
                return CreatePlaceholder(pixelWidth, pixelHeight, "Shapefile not found");

            var land = new SolidColorBrush(landColor ?? Color.FromRgb(58, 114, 76));
            var water = new SolidColorBrush(waterColor ?? Color.FromRgb(20, 22, 30));
            var stroke = new SolidColorBrush(strokeColor ?? Color.FromArgb(100, 0, 0, 0));
            land.Freeze(); water.Freeze(); stroke.Freeze();

            // Compute viewing window in degrees
            double minLon, maxLon, minLat, maxLat;
            if (centerLon.HasValue && centerLat.HasValue)
            {
                double deg = (radiusNm / 60.0) * padRatio;
                minLon = centerLon.Value - deg;
                maxLon = centerLon.Value + deg;
                minLat = centerLat.Value - deg;
                maxLat = centerLat.Value + deg;
            }
            else
            {
                minLon = -180; maxLon = 180;
                minLat = -85; maxLat = 85;
            }

            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                // Background water
                dc.DrawRectangle(water, null, new Rect(0, 0, pixelWidth, pixelHeight));

                // Coastlines (land)
                try
                {
                    var reader = new ShapefileDataReader(shpPath, GeometryFactory.Default);
                    while (reader.Read())
                    {
                        var geom = reader.Geometry;
                        if (geom == null) continue;

                        if (geom is Polygon poly)
                        {
                            DrawPolygonClipped(dc, poly, pixelWidth, pixelHeight, land, stroke,
                                               minLon, maxLon, minLat, maxLat);
                        }
                        else if (geom is MultiPolygon mp)
                        {
                            for (int i = 0; i < mp.NumGeometries; i++)
                            {
                                if (mp.GetGeometryN(i) is Polygon p)
                                {
                                    DrawPolygonClipped(dc, p, pixelWidth, pixelHeight, land, stroke,
                                                       minLon, maxLon, minLat, maxLat);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    return CreatePlaceholder(pixelWidth, pixelHeight, "Shapefile error:\n" + ex.Message);
                }

                // ---- MARKER ORDER ----
                // 1) Blue dots for other platforms (bottom-most markers)
                if (otherPlatformMarkers != null)
                {
                    foreach (var (lon, lat) in otherPlatformMarkers)
                    {
                        if (!Inside(lon, lat, minLon, maxLon, minLat, maxLat)) continue;
                        var (x, y) = LonLatToCanvas(lon, lat, minLon, maxLon, minLat, maxLat, pixelWidth, pixelHeight);
                     //   DrawBlueDiamond(dc, x, y, size: 9);
                        DrawBlueDot(dc, x, y, radius: 3.0);
                    }
                }

                // 2) White cross markers (e.g., exercise center)
                if (markers != null)
                {
                    foreach (var (lon, lat) in markers)
                    {
                        if (!Inside(lon, lat, minLon, maxLon, minLat, maxLat)) continue;
                        var (x, y) = LonLatToCanvas(lon, lat, minLon, maxLon, minLat, maxLat, pixelWidth, pixelHeight);
                        DrawWhiteCross(dc, x, y, size: 9, opacity: crossOpacity);
                    }
                }

                // 3) Yellow diamonds for LaunchPlatform(s) (top-most markers)
                if (launchMarkers != null)
                {
                    foreach (var (lon, lat) in launchMarkers)
                    {
                        if (!Inside(lon, lat, minLon, maxLon, minLat, maxLat)) continue;
                        var (x, y) = LonLatToCanvas(lon, lat, minLon, maxLon, minLat, maxLat, pixelWidth, pixelHeight);
                        DrawDiamond(dc, x, y, size: 9);
                    }
                }
            }

            var rtb = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }

        // ---------------------- Auto-fit helpers (for hover zoom) ----------------------

        /// <summary>
        /// Compute a view (center lon/lat and radius in NM) that fits all provided platform markers.
        /// Uses farthest-pair great-circle distance for radius, and geographic midpoint for center.
        /// </summary>
        public static (double centerLon, double centerLat, double radiusNm) ComputeAutoView(
            IEnumerable<(double lon, double lat)> launchMarkers,
            IEnumerable<(double lon, double lat)> otherPlatformMarkers,
            double minRadiusNm = 60,
            double paddingNm = 30)
        {
            var pts = new List<(double lon, double lat)>();
            if (launchMarkers != null) pts.AddRange(launchMarkers);
            if (otherPlatformMarkers != null) pts.AddRange(otherPlatformMarkers);

            if (pts.Count == 0)
            {
                // Fallback: world-ish view
                return (0, 0, Math.Max(minRadiusNm, 600));
            }

            // If only one point, center there with min radius
            if (pts.Count == 1)
            {
                var (lon, lat) = pts[0];
                return (lon, lat, minRadiusNm);
            }

            // Find farthest pair by great-circle distance
            double maxNm = 0;
            int iMax = 0, jMax = 1;
            for (int i = 0; i < pts.Count; i++)
            {
                for (int j = i + 1; j < pts.Count; j++)
                {
                    double d = HaversineNm(pts[i].lat, pts[i].lon, pts[j].lat, pts[j].lon);
                    if (d > maxNm)
                    {
                        maxNm = d;
                        iMax = i; jMax = j;
                    }
                }
            }

            // Center at geographic midpoint of farthest pair (good enough for local extents)
            var mid = GeographicMidpoint(pts[iMax].lat, pts[iMax].lon, pts[jMax].lat, pts[jMax].lon);

            // Radius: at least half of farthest distance, plus padding; clamp to minRadius
            double radius = Math.Max(minRadiusNm, (maxNm / 2.0) + paddingNm);

            // Make sure ALL points fit (if outliers make it larger)
            foreach (var p in pts)
            {
                double d = HaversineNm(mid.lat, mid.lon, p.lat, p.lon);
                if (d + paddingNm > radius) radius = d + paddingNm;
            }

            return (mid.lon, mid.lat, radius);
        }

        /// <summary>
        /// Convenience: parses the exercise file to get launch + other platforms and returns an auto-fit view.
        /// </summary>
        public static (double centerLon, double centerLat, double radiusNm) ComputeAutoViewFromExercise(
            string exercisePath,
            double minRadiusNm = 60,
            double paddingNm = 30)
        {
            var launch = TryReadLaunchPlatformPositions(exercisePath);
            var other = TryReadOtherPlatformPositions(exercisePath);
            return ComputeAutoView(launch, other, minRadiusNm, paddingNm);
        }

        // Great-circle distance in nautical miles
        private static double HaversineNm(double lat1, double lon1, double lat2, double lon2)
        {
            const double R_km = 6371.0088;   // mean Earth radius
            const double KM_PER_NM = 1.852;

            double dLat = ToRad(lat2 - lat1);
            double dLon = ToRad(lon2 - lon1);
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                       Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2)) *
                       Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            double km = R_km * c;
            return km / KM_PER_NM;
        }

        // Geographic midpoint between two points
        private static (double lat, double lon) GeographicMidpoint(double lat1, double lon1, double lat2, double lon2)
        {
            double φ1 = ToRad(lat1), λ1 = ToRad(lon1);
            double φ2 = ToRad(lat2), λ2 = ToRad(lon2);

            double x1 = Math.Cos(φ1) * Math.Cos(λ1);
            double y1 = Math.Cos(φ1) * Math.Sin(λ1);
            double z1 = Math.Sin(φ1);

            double x2 = Math.Cos(φ2) * Math.Cos(λ2);
            double y2 = Math.Cos(φ2) * Math.Sin(λ2);
            double z2 = Math.Sin(φ2);

            double x = (x1 + x2) / 2.0;
            double y = (y1 + y2) / 2.0;
            double z = (z1 + z2) / 2.0;

            double hyp = Math.Sqrt(x * x + y * y);
            double lat = Math.Atan2(z, hyp);
            double lon = Math.Atan2(y, x);

            return (ToDeg(lat), ToDeg(lon));
        }

        private static double ToRad(double deg) => deg * Math.PI / 180.0;
        private static double ToDeg(double rad) => rad * 180.0 / Math.PI;

        // ---------------- Polygon drawing (clamped so coasts remain continuous) ----------------

        private static void DrawPolygonClipped(DrawingContext dc, Polygon poly, int w, int h,
                                               Brush fill, Brush stroke,
                                               double minLon, double maxLon, double minLat, double maxLat)
        {
            var sg = new StreamGeometry { FillRule = FillRule.EvenOdd };
            using (var ctx = sg.Open())
            {
                AddRingClamped(ctx, poly.ExteriorRing, w, h, minLon, maxLon, minLat, maxLat);
                for (int i = 0; i < poly.NumInteriorRings; i++)
                    AddRingClamped(ctx, poly.GetInteriorRingN(i), w, h, minLon, maxLon, minLat, maxLat);
            }
            sg.Freeze();
            dc.DrawGeometry(fill, new Pen(stroke, 0.5), sg);
        }

        private static void AddRingClamped(StreamGeometryContext ctx, LineString ring, int w, int h,
                                           double minLon, double maxLon, double minLat, double maxLat)
        {
            if (ring == null || ring.NumPoints == 0) return;

            bool started = false;
            for (int i = 0; i < ring.NumPoints; i++)
            {
                var c = ring.GetCoordinateN(i);
                double lon = Clamp(c.X, minLon, maxLon);
                double lat = Clamp(c.Y, minLat, maxLat);

                var (x, y) = LonLatToCanvas(lon, lat, minLon, maxLon, minLat, maxLat, w, h);
                var pt = new WpfPoint(x, y);

                if (!started)
                {
                    ctx.BeginFigure(pt, isFilled: true, isClosed: true);
                    started = true;
                }
                else
                {
                    ctx.LineTo(pt, isStroked: false, isSmoothJoin: false);
                }
            }
        }

        // -------------------------------- Marker drawings --------------------------------

        private static void DrawWhiteCross(DrawingContext dc, double x, double y, double size, double opacity = 0.85)
        {
            double half = size / 2.0;

            var brush = new SolidColorBrush(Color.FromArgb(
                (byte)Math.Max(0, Math.Min(255, opacity * 255.0)),
                255, 255, 255));
            brush.Freeze();

            var pen = new Pen(brush, 2.0);
            pen.Freeze();

            dc.DrawLine(pen, new WpfPoint(x - half, y), new WpfPoint(x + half, y));
            dc.DrawLine(pen, new WpfPoint(x, y - half), new WpfPoint(x, y + half));
        }

        private static void DrawDiamond(DrawingContext dc, double x, double y, double size)
        {
            var half = size / 2.0;
            var sg = new StreamGeometry();
            using (var ctx = sg.Open())
            {
                ctx.BeginFigure(new WpfPoint(x, y - half), isFilled: true, isClosed: true);
                ctx.LineTo(new WpfPoint(x + half, y), isStroked: true, isSmoothJoin: false);
                ctx.LineTo(new WpfPoint(x, y + half), isStroked: true, isSmoothJoin: false);
                ctx.LineTo(new WpfPoint(x - half, y), isStroked: true, isSmoothJoin: false);
            }
            sg.Freeze();

            var fill = new SolidColorBrush(Color.FromRgb(255, 215, 0)); // yellow/gold
            fill.Freeze();

            dc.DrawGeometry(fill, new Pen(Brushes.Black, 1.2), sg);
        }

        private static void DrawBlueDiamond(DrawingContext dc, double x, double y, double size)
        {
            var half = size / 2.0;
            var sg = new StreamGeometry();
            using (var ctx = sg.Open())
            {
                ctx.BeginFigure(new WpfPoint(x, y - half), isFilled: true, isClosed: true);
                ctx.LineTo(new WpfPoint(x + half, y), isStroked: true, isSmoothJoin: false);
                ctx.LineTo(new WpfPoint(x, y + half), isStroked: true, isSmoothJoin: false);
                ctx.LineTo(new WpfPoint(x - half, y), isStroked: true, isSmoothJoin: false);
            }
            sg.Freeze();

            var fill = new SolidColorBrush(Color.FromRgb(70, 160, 255)); // yellow/gold
            fill.Freeze();

            dc.DrawGeometry(fill, new Pen(Brushes.Black, 1.2), sg);
        }

        private static void DrawBlueDot(DrawingContext dc, double x, double y, double radius)
        {
            var fill = new SolidColorBrush(Color.FromRgb(70, 160, 255)); // bright-ish blue
            fill.Freeze();
            dc.DrawEllipse(fill, new Pen(Brushes.White, 1.0), new WpfPoint(x, y), radius, radius);
        }

        // -------------------------------- Utilities --------------------------------

        private static (double x, double y) LonLatToCanvas(double lon, double lat,
                                                           double minLon, double maxLon,
                                                           double minLat, double maxLat,
                                                           int w, int h)
        {
            // Equirectangular (plate carrée) mapping for thumbnails
            double x = (lon - minLon) / (maxLon - minLon) * w;
            double y = (1 - (lat - minLat) / (maxLat - minLat)) * h;
            return (x, y);
        }

        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

        private static bool Inside(double lon, double lat,
                                   double minLon, double maxLon,
                                   double minLat, double maxLat)
        {
            return lon >= minLon && lon <= maxLon && lat >= minLat && lat <= maxLat;
        }

        private static ImageSource CreatePlaceholder(int w, int h, string message)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(25, 27, 34)), null, new Rect(0, 0, w, h));
                var ft = new FormattedText(
                    message,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"),
                    12,
                    Brushes.LightGray,
                    1.25);
                dc.DrawText(ft, new WpfPoint(12, 12));
            }
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }

        // ---------------------------- ECEF <-> Lat/Lon & Parsing ----------------------------

        /// <summary>Reads &lt;Properties&gt;/&lt;CenterPosition&gt; ECEF and converts to lon/lat.</summary>
        public static bool TryReadExerciseCenter(string xmlPath, out double lonDeg, out double latDeg)
        {
            lonDeg = latDeg = 0;
            try
            {
                var xml = File.ReadAllText(xmlPath);
                double x = Extract(xml, "<X>", "</X>", 1);
                double y = Extract(xml, "<Y>", "</Y>", 1);
                double z = Extract(xml, "<Z>", "</Z>", 1);
                EcefToGeodeticWgs84(x, y, z, out latDeg, out lonDeg);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>All platforms with Role == LaunchPlatform, converted to lon/lat.</summary>
        public static List<(double lon, double lat)> TryReadLaunchPlatformPositions(string exercisePath)
        {
            var list = new List<(double lon, double lat)>();
            try
            {
                var doc = XDocument.Load(exercisePath);
                foreach (var p in doc.Descendants("PlatformManager").Descendants("Platforms").Descendants("Platform"))
                {
                    var role = p.Element("Role")?.Value?.Trim();
                    if (!string.Equals(role, "LaunchPlatform", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (TryGetPositionECEF(p, out var x, out var y, out var z))
                    {
                        EcefToGeodeticWgs84(x, y, z, out var lat, out var lon);
                        list.Add((lon, lat));
                    }
                }
            }
            catch { }
            return list;
        }

        /// <summary>All platforms that have a Position and Role != LaunchPlatform, converted to lon/lat.</summary>
        public static List<(double lon, double lat)> TryReadOtherPlatformPositions(string exercisePath)
        {
            var list = new List<(double lon, double lat)>();
            try
            {
                var doc = XDocument.Load(exercisePath);
                foreach (var p in doc.Descendants("PlatformManager").Descendants("Platforms").Descendants("Platform"))
                {
                    var role = p.Element("Role")?.Value?.Trim();
                    if (string.Equals(role, "LaunchPlatform", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (TryGetPositionECEF(p, out var x, out var y, out var z))
                    {
                        EcefToGeodeticWgs84(x, y, z, out var lat, out var lon);
                        list.Add((lon, lat));
                    }
                }
            }
            catch { }
            return list;
        }

        private static bool TryGetPositionECEF(XElement platform, out double x, out double y, out double z)
        {
            x = y = z = 0;
            var pos = platform.Element("Position");
            if (pos == null) return false;

            bool okX = double.TryParse(pos.Element("X")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out x);
            bool okY = double.TryParse(pos.Element("Y")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out y);
            bool okZ = double.TryParse(pos.Element("Z")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out z);
            return okX && okY && okZ;
        }

        /// <summary>ECEF (meters, WGS-84) → geodetic lat/lon in degrees.</summary>
        public static void EcefToGeodeticWgs84(double x, double y, double z, out double latDeg, out double lonDeg)
        {
            const double a = 6378137.0;            // semi-major axis
            const double e2 = 6.69437999014e-3;     // first eccentricity^2

            double lon = Math.Atan2(y, x);
            double p = Math.Sqrt(x * x + y * y);
            double lat = Math.Atan2(z, p * (1 - e2));

            double latPrev;
            do
            {
                latPrev = lat;
                double sinLat = Math.Sin(lat);
                double N = a / Math.Sqrt(1 - e2 * sinLat * sinLat);
                lat = Math.Atan2(z + e2 * N * sinLat, p);
            } while (Math.Abs(lat - latPrev) > 1e-12);

            latDeg = lat * 180.0 / Math.PI;
            lonDeg = lon * 180.0 / Math.PI;
        }

        private static double Extract(string s, string startTag, string endTag, int occurrence)
        {
            int idx = -1;
            for (int k = 0; k < occurrence; k++)
            {
                idx = s.IndexOf(startTag, idx + 1, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) throw new InvalidOperationException("Tag not found: " + startTag);
            }
            int a = idx + startTag.Length;
            int b = s.IndexOf(endTag, a, StringComparison.OrdinalIgnoreCase);
            var sub = s.Substring(a, b - a).Trim();
            return double.Parse(sub, CultureInfo.InvariantCulture);
        }
    }
}
