using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Lively.Views
{
    /// <summary>
    /// A screensaver that shows the pictures the person starred in Iceberg.
    ///
    /// It owns nothing: the folder it is pointed at is filled by
    /// <see cref="Services.FavouriteScreensaverCache"/>, and every change of
    /// picture re-reads that folder. A download can therefore finish at any
    /// time and simply joins the slideshow.
    /// </summary>
    public partial class FavouriteScreensaverWindow : Window
    {
        private static readonly string[] PictureExtensions =
            [".jpg", ".jpeg", ".png", ".bmp", ".webp", ".gif"];

        private readonly string folder;
        private readonly Random random = new();
        private readonly DispatcherTimer timer;
        private string current;
        private bool switching;

        public FavouriteScreensaverWindow(string folder)
        {
            InitializeComponent();

            this.folder = folder;

            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            timer.Tick += (s, e) => ShowNext();

            Loaded += (s, e) =>
            {
                ShowNext();
                timer.Start();
            };

            Closed += (s, e) => timer.Stop();
        }

        private void ShowNext()
        {
            if (switching)
            {
                return;
            }

            var pictures = FindPictures();

            if (pictures.Count == 0)
            {
                Hint.Visibility = Visibility.Visible;
                return;
            }

            Hint.Visibility = Visibility.Collapsed;

            var next = pictures[random.Next(pictures.Count)];

            // Do not show the same picture twice in a row when there is a choice.
            if (pictures.Count > 1)
            {
                var guard = 0;
                while (string.Equals(next, current, StringComparison.OrdinalIgnoreCase) && guard++ < 10)
                {
                    next = pictures[random.Next(pictures.Count)];
                }
            }

            Show(pictures, next);
        }

        private void Show(IReadOnlyList<string> pictures, string path)
        {
            if (!TryLoad(path, out var bitmap))
            {
                // A half-written or unsupported file: try a different one.
                var fallback = pictures.FirstOrDefault(
                    p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase));

                if (fallback is null || !TryLoad(fallback, out bitmap))
                {
                    return;
                }

                path = fallback;
            }

            switching = true;

            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(500));
            fadeOut.Completed += (s, e) =>
            {
                Picture.Source = bitmap;

                var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(900));
                fadeIn.Completed += (s2, e2) => switching = false;
                Picture.BeginAnimation(OpacityProperty, fadeIn);
            };

            Picture.BeginAnimation(OpacityProperty, fadeOut);
            current = path;
        }

        private static bool TryLoad(string path, out BitmapImage bitmap)
        {
            bitmap = null;

            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(path, UriKind.Absolute);
                image.EndInit();
                image.Freeze();

                bitmap = image;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private List<string> FindPictures()
        {
            try
            {
                if (!Directory.Exists(folder))
                {
                    return new List<string>();
                }

                return Directory
                    .EnumerateFiles(folder)
                    .Where(f => PictureExtensions.Contains(
                        Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }
    }
}
