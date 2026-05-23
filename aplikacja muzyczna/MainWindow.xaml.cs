using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using WinForms = System.Windows.Forms;
using TagLib;
using System.Windows.Media.Imaging;
using Microsoft.VisualBasic;
//Install-Package TagLibSharp -Version 2.3.0 \\ w "konsola menedżera pa
namespace aplikacja_muzyczna
{
    public class AudioItem : INotifyPropertyChanged
    {
        public string Name { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public string? Subtitle { get; set; }
        public ImageSource? CoverSmall { get; set; }
        public ImageSource? CoverLarge { get; set; }
        private string _performer = string.Empty;
        public string Performer
        {
            get => _performer;
            set
            {
                if (_performer != value)
                {
                    _performer = value;
                    OnPropertyChanged(nameof(Performer));
                }
            }
        }

        private string _album = string.Empty;
        public string Album
        {
            get => _album;
            set
            {
                if (_album != value)
                {
                    _album = value;
                    OnPropertyChanged(nameof(Album));
                }
            }
        }

        private TimeSpan? _duration;
        public TimeSpan? Duration
        {
            get => _duration;
            set
            {
                if (_duration != value)
                {
                    _duration = value;
                    OnPropertyChanged(nameof(Duration));
                    OnPropertyChanged(nameof(DisplayDuration));
                }
            }
        }

        public string DisplayDuration => Duration.HasValue ? Duration.Value.ToString(@"mm\:ss") : "-";
        public string ImageName => string.IsNullOrWhiteSpace(FilePath) ? "" : Path.GetFileName(FilePath);

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
    public partial class MainWindow : Window
    {
        private static BitmapImage CreateBitmapImage(byte[] data, int maxDimension)
        {
            try
            {
                using var ms = new MemoryStream(data);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.DecodePixelWidth = maxDimension;
                bmp.DecodePixelHeight = maxDimension;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return new BitmapImage();
            }
        }
        private MediaPlayer mediaPlayer = new MediaPlayer();
        private ObservableCollection<AudioItem> audioFiles = new ObservableCollection<AudioItem>();
        private DispatcherTimer timer;
        private int currentIndex = -1;
        private bool isPlaying = false;
        private readonly string[] allowedExt = new[] { ".mp3" };
        private readonly Random rng = new Random();
        private bool playingAlbum = false;
        private string? currentAlbum = null;
        private System.Collections.Generic.List<AudioItem> currentAlbumItems = new System.Collections.Generic.List<AudioItem>();
        private int currentAlbumIndex = -1;
        private TimeSpan? pendingSeek = null;
        private bool pendingResume = false;

        public MainWindow()
        {
            InitializeComponent();

            FilesList.ItemsSource = audioFiles;

            // ustaw początkową głośność na wartość suwaka
            mediaPlayer.Volume = (VolumeSlider?.Value ?? 50) / 100.0;
            if (VolumeValueText != null) VolumeValueText.Text = $"{(int)(VolumeSlider?.Value ?? 50)}%";

            timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromSeconds(1);
            timer.Tick += timer_Tick;
            timer.Start();

            mediaPlayer.MediaEnded += MediaPlayer_MediaEnded;
            mediaPlayer.MediaOpened += MediaPlayer_MediaOpened;
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            var val = e.NewValue;
            mediaPlayer.Volume = val / 100.0;
            if (VolumeValueText != null)
                VolumeValueText.Text = $"{(int)val}%";
        }

        private void btnOpenAudioFile_Click(object sender, RoutedEventArgs e)
        {
            SelectFolderClick();
        }

        public void SelectFolderClick()
        {
            audioFiles.Clear();

            using var dlg = new WinForms.FolderBrowserDialog();
            dlg.Description = "Wybierz folder z plikami audio";
            dlg.ShowNewFolderButton = false;

            if (dlg.ShowDialog() != WinForms.DialogResult.OK)
            {
                return;
            }

            var selectedPath = dlg.SelectedPath;

            var files = Directory.EnumerateFiles(selectedPath, "*.*", SearchOption.AllDirectories)
                                 .Where(f => allowedExt.Contains(Path.GetExtension(f).ToLowerInvariant()))
                                 .Select(f => new FileInfo(f))
                                 .ToList();

            long totalBytes = files.Sum(fi => fi.Length);


            var items = new System.Collections.Generic.List<AudioItem>();
            foreach (var fi in files)
            {
                var item = new AudioItem
                {
                    Name = Path.GetFileName(fi.FullName),
                    FilePath = fi.FullName,
                    SizeBytes = fi.Length,
                    Duration = null,
                    Album = GetAlbumFromFileInfo(fi),
                    Performer = NormalizePerformer(ExtractPerformerFromFileName(Path.GetFileNameWithoutExtension(fi.FullName), fi))
                };

                try
                {
                    using var tfile = TagLib.File.Create(fi.FullName);
                    // Tytuł
                    if (!string.IsNullOrWhiteSpace(tfile.Tag.Title))
                        item.Name = tfile.Tag.Title;

                    // Wykonawca
                    var perf = tfile.Tag.FirstPerformer ?? tfile.Tag.Performers?.FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(perf))
                        item.Performer = NormalizePerformer(perf);

                    // Album
                    if (!string.IsNullOrWhiteSpace(tfile.Tag.Album))
                        item.Album = tfile.Tag.Album;

                    // Czas trwania
                    try
                    {
                        var dur = tfile.Properties?.Duration;
                        if (dur != null && dur != TimeSpan.Zero)
                            item.Duration = dur;
                    }
                    catch { }

                    // Okładka
                    try
                    {
                        var pics = tfile.Tag.Pictures;
                        if (pics != null && pics.Length > 0 && pics[0]?.Data != null)
                        {
                            var data = pics[0].Data.Data;
                            item.CoverSmall = CreateBitmapImage(data, 48);
                            item.CoverLarge = CreateBitmapImage(data, 300);
                        }
                    }
                    catch { }

                    // Napis (UserTextInformationFrame "Napis")
                    try
                    {
                        var id3 = tfile.GetTag(TagLib.TagTypes.Id3v2) as TagLib.Id3v2.Tag;
                        if (id3 != null)
                        {
                            var frame = TagLib.Id3v2.UserTextInformationFrame.Get(id3, "Napis", false);
                            if (frame != null && frame.Text != null && frame.Text.Length > 0)
                                item.Subtitle = string.Join("\n", frame.Text.Where(s => !string.IsNullOrEmpty(s)));
                        }
                        // fallback: jeśli brak ramki Napis, użyj Comment
                        if (string.IsNullOrWhiteSpace(item.Subtitle) && !string.IsNullOrWhiteSpace(tfile.Tag.Comment))
                        {
                            item.Subtitle = tfile.Tag.Comment;
                        }
                    }
                    catch { }
                }
                catch
                {
                    // jeśli TagLib nie obsługuje pliku, użyj heurystyk już ustawionych
                }

                items.Add(item);
            }

            foreach (var it in items.OrderBy(a => a.Name))
                audioFiles.Add(it);

            TotalFilesText.Text = $"File count: {audioFiles.Count}";
            LengthFilesText.Text = $"Total size: {FormatBytes(totalBytes)}";

            RefreshAlbumsPanel();
        }

        private static string ExtractPerformerFromFileName(string nameNoExt, FileInfo fi)
        {
            if (string.IsNullOrWhiteSpace(nameNoExt)) return "";
            if (nameNoExt.Contains(" - "))
            {
                return nameNoExt.Split(new[] { " - " }, StringSplitOptions.None)[0];
            }
            // spróbuj rodzica katalogu jako wykonawca
            try
            {
                if (fi.Directory?.Parent != null)
                    return fi.Directory.Parent.Name;
            }
            catch { }
            return "";
        }

        private static string NormalizePerformer(string perf)
        {
            if (string.IsNullOrWhiteSpace(perf)) return "-";
            // jeśli nazwa katalogu zwróciła coś ogólnego jak 'Desktop', zamień na '-'
            if (string.Equals(perf, "Desktop", StringComparison.InvariantCultureIgnoreCase))
                return "-";
            return perf;
        }

        private static string GetAlbumFromFileInfo(FileInfo fi)
        {
            try
            {

                if (fi.Directory?.Parent != null)
                {

                    var parent = "-";
                    if (!string.IsNullOrWhiteSpace(parent))
                        return parent;
                }
                if (fi.Directory != null && !string.IsNullOrWhiteSpace(fi.Directory.Name))
                    return fi.Directory.Name;
            }
            catch { }
            return "-";
        }

        // (method removed to avoid duplicate CreateBitmapImage definitions)

        private void RefreshAlbumsPanel()
        {
            if (AlbumsPanel == null) return;

            AlbumsPanel.Children.Clear();
            var albums = audioFiles.Select(a => string.IsNullOrWhiteSpace(a.Album) ? "-" : a.Album)
                                    .Where(a => a != null)
                                    .Distinct()
                                    .OrderBy(a => a)
                                    .ToList();

            if (!albums.Any())
            {
                AlbumsPanel.Children.Add(new TextBlock { Text = "Brak albumów", Margin = new Thickness(4) });
                return;
            }

            foreach (var alb in albums)
            {
                var rb = new System.Windows.Controls.RadioButton { Content = alb, GroupName = "AlbumsGroup", Margin = new Thickness(2) };
                AlbumsPanel.Children.Add(rb);
            }
        }

        private string? GetSelectedAlbum()
        {
            if (AlbumsPanel == null) return null;
            foreach (var child in AlbumsPanel.Children)
            {
                if (child is System.Windows.Controls.RadioButton rb && rb.IsChecked == true)
                    return rb.Content?.ToString();
            }
            return null;
        }

        private void btnPlayAlbum_Click(object sender, RoutedEventArgs e)
        {
            var album = GetSelectedAlbum();
            if (string.IsNullOrWhiteSpace(album))
            {
                return;
            }
            // zachowaj kolejność z audioFiles (np. po losowym przetasowaniu) — nie sortuj po nazwie
            var matches = audioFiles.Where(a => string.Equals(a.Album, album, StringComparison.InvariantCultureIgnoreCase)).ToList();
            if (!matches.Any()) return;

            // przygotuj listę albumu i rozpocznij od pierwszego
            currentAlbumItems = matches.ToList();
            currentAlbum = album;
            currentAlbumIndex = 0;
            playingAlbum = true;

            var first = currentAlbumItems[currentAlbumIndex];
            var idx = audioFiles.IndexOf(first);
            if (idx >= 0)
            {
                PlayAt(idx, preserveAlbum: true);
            }
        }

        private void btnChangeAlbum_Click(object sender, RoutedEventArgs e)
        {
            if (currentIndex < 0 || currentIndex >= audioFiles.Count)
            {
                System.Windows.MessageBox.Show("Brak aktualnie grającego pliku.", "Brak pliku", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            // Użyj InputBox z Microsoft.VisualBasic.Interaction
            var existing = audioFiles.Select(a => a.Album).Where(a => !string.IsNullOrWhiteSpace(a)).Distinct().OrderBy(a => a).ToList();
            var defaultVal = GetSelectedAlbum() ?? "";
            string prompt = "Wpisz nowy album lub wybierz z istniejących:\n" + string.Join("; ", existing);
            string result = Interaction.InputBox(prompt, "Zmień album", defaultVal, 200, 200);
            if (string.IsNullOrWhiteSpace(result))
            {
                // nic nie zmieniamy
                return;
            }

            var item = audioFiles[currentIndex];
            item.Album = result.Trim();
            RefreshAlbumsPanel();
            System.Windows.MessageBox.Show($"Zmieniono album aktualnego pliku na '{item.Album}'.", "Zmieniono album", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        void timer_Tick(object? sender, EventArgs e)
        {
            if (mediaPlayer.Source != null && mediaPlayer.NaturalDuration.HasTimeSpan)
            {
                lblStatus.Content = String.Format("{0} / {1}",
                    mediaPlayer.Position.ToString(@"mm\:ss"),
                    mediaPlayer.NaturalDuration.TimeSpan.ToString(@"mm\:ss"));
            }
            else if (mediaPlayer.Source != null)
            {
                lblStatus.Content = "Loading media...";
            }
            else
            {
                lblStatus.Content = "00:00 / 00:00";
            }
        }

        private void btnPlay_Click(object sender, RoutedEventArgs e)
        {
            if (!audioFiles.Any())
            {
                System.Windows.MessageBox.Show("Brak plików w liście. Wybierz folder z audio.", "Brak plików", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (mediaPlayer.Source == null)
            {
                if (FilesList.SelectedIndex >= 0)
                    PlayAt(FilesList.SelectedIndex);
                else
                    PlayAt(0);
            }
            else
            {
                if (isPlaying)
                {
                    mediaPlayer.Pause();
                    btnPlay.Content = "Play";
                    isPlaying = false;
                }
                else
                {
                    mediaPlayer.Play();
                    btnPlay.Content = "Pause";
                    isPlaying = true;
                }
            }
        }

        private void btnNext_Click(object sender, RoutedEventArgs e)
        {
            if (!audioFiles.Any()) return;
            if (playingAlbum && currentAlbumItems != null && currentAlbumItems.Any() && currentAlbumIndex >= 0)
            {
                // odtwórz następny utwór z albumu
                if (currentAlbumIndex + 1 < currentAlbumItems.Count)
                {
                    currentAlbumIndex++;
                    var next = currentAlbumItems[currentAlbumIndex];
                    var idx = audioFiles.IndexOf(next);
                    if (idx >= 0) PlayAt(idx, preserveAlbum: true);
                }
                else
                {
                    // koniec albumu
                    mediaPlayer.Stop();
                    isPlaying = false;
                    btnPlay.Content = "Play";
                    currentIndex = -1;
                    playingAlbum = false;
                    currentAlbum = null;
                    currentAlbumItems.Clear();
                    currentAlbumIndex = -1;
                    System.Windows.MessageBox.Show("Koniec albumu.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            else
            {
                int nextIndex = (currentIndex >= 0) ? currentIndex + 1 : 0;

                if (nextIndex < audioFiles.Count)
                {
                    PlayAt(nextIndex);
                }
                else
                {
                    // koniec listy — zatrzymaj
                    mediaPlayer.Stop();
                    isPlaying = false;
                    btnPlay.Content = "Play";
                    currentIndex = -1;
                    System.Windows.MessageBox.Show("Koniec listy.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }

        private void PlayAt(int index, bool preserveAlbum = false)
        {
            if (index < 0 || index >= audioFiles.Count) return;

            currentIndex = index;
            var item = audioFiles[currentIndex];
            try
            {
                mediaPlayer.Open(new Uri(item.FilePath));
                FilesList.SelectedIndex = currentIndex;
                FilesList.ScrollIntoView(item);
                mediaPlayer.Play();
                isPlaying = true;
                btnPlay.Content = "Pause";
                // pokaż nazwę pliku w CurrentFileText
                if (CurrentFileText != null)
                {
                    if (preserveAlbum && !string.IsNullOrWhiteSpace(currentAlbum))
                        CurrentFileText.Text = $"{currentAlbum} - {item.Name ?? Path.GetFileName(item.FilePath)}";
                    else
                        CurrentFileText.Text = item.Name ?? Path.GetFileName(item.FilePath);
                }

                // pokaż napisy w SubtitleText
                if (SubtitleText != null)
                {
                    SubtitleText.Text = string.IsNullOrWhiteSpace(item.Subtitle) ? "Brak napisów" : item.Subtitle;
                }

                // pokaż powiększony cover
                if (NowPlayingCover != null)
                {
                    NowPlayingCover.Source = item.CoverLarge;
                }

                // jeśli nie preserveAlbum, oznaczamy, że to nie jest odtwarzanie albumu
                if (!preserveAlbum)
                {
                    playingAlbum = false;
                    currentAlbum = null;
                    currentAlbumItems.Clear();
                    currentAlbumIndex = -1;
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Nie można odtworzyć pliku: {ex.Message}", "Błąd odtwarzania", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void MediaPlayer_MediaEnded(object? sender, EventArgs e)
        {
            if (playingAlbum && currentAlbumItems != null && currentAlbumIndex >= 0)
            {
                if (currentAlbumIndex + 1 < currentAlbumItems.Count)
                {
                    currentAlbumIndex++;
                    var next = currentAlbumItems[currentAlbumIndex];
                    var idx = audioFiles.IndexOf(next);
                    if (idx >= 0) PlayAt(idx, preserveAlbum: true);
                }
                else
                {
                    mediaPlayer.Stop();
                    isPlaying = false;
                    btnPlay.Dispatcher.Invoke(() => btnPlay.Content = "Play");
                    currentIndex = -1;
                    playingAlbum = false;
                    currentAlbum = null;
                    currentAlbumItems.Clear();
                    currentAlbumIndex = -1;
                }
            }
            else
            {
                if (currentIndex + 1 < audioFiles.Count)
                {
                    PlayAt(currentIndex + 1);
                }
                else
                {
                    mediaPlayer.Stop();
                    isPlaying = false;
                    btnPlay.Dispatcher.Invoke(() => btnPlay.Content = "Play");
                    currentIndex = -1;
                }
            }
        }

        private void MediaPlayer_MediaOpened(object? sender, EventArgs e)
        {
            if (currentIndex >= 0 && currentIndex < audioFiles.Count && mediaPlayer.NaturalDuration.HasTimeSpan)
            {
                audioFiles[currentIndex].Duration = mediaPlayer.NaturalDuration.TimeSpan;
            }
            // jeśli mamy pendingSeek ustawiony, ustaw pozycję i wznowienie
            try
            {
                if (pendingSeek.HasValue)
                {
                    mediaPlayer.Position = pendingSeek.Value;
                    pendingSeek = null;
                }
                if (pendingResume)
                {
                    mediaPlayer.Play();
                    isPlaying = true;
                    btnPlay.Content = "Pause";
                    pendingResume = false;
                }
            }
            catch { }
        }

        private void FilesList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (FilesList.SelectedIndex >= 0)
            {
                PlayAt(FilesList.SelectedIndex);
            }
        }
        private void btnSubtitle_Click(object sender, RoutedEventArgs e)
        {
            if (currentIndex < 0 || currentIndex >= audioFiles.Count)
            {
                System.Windows.MessageBox.Show(
                    "Brak aktualnie wybranego pliku.",
                    "Brak pliku",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                return;
            }

            var item = audioFiles[currentIndex];

            string existing =
                string.IsNullOrWhiteSpace(item.Subtitle)
                ? ""
                : item.Subtitle;

            string result = Interaction.InputBox(
                "Wpisz nowe napisy:",
                "Zmień napisy",
                existing,
                200,
                200);

            // anulowano
            if (result == null)
                return;

            string? newText =
                string.IsNullOrWhiteSpace(result)
                ? null
                : result.Trim();

            bool wasPlaying = isPlaying;
            TimeSpan currentPos = mediaPlayer.Position;

            try
            {
                //
                // WAŻNE:
                // najpierw zatrzymaj i zamknij player
                //
                mediaPlayer.Stop();
                mediaPlayer.Close();

                isPlaying = false;

                //
                // daj systemowi chwilę na zwolnienie pliku
                //
                System.Threading.Thread.Sleep(300);

                //
                // zapis tagów
                //
                using (var file = TagLib.File.Create(item.FilePath))
                {
                    //
                    // wymuś ID3v2
                    //
                    var id3 =
                        file.GetTag(
                            TagTypes.Id3v2,
                            true) as TagLib.Id3v2.Tag;

                    if (id3 != null)
                    {
                        //
                        // usuń stary frame
                        //
                        var oldFrame =
                            TagLib.Id3v2.UserTextInformationFrame.Get(
                                id3,
                                "Napis",
                                false);

                        if (oldFrame != null)
                        {
                            id3.RemoveFrame(oldFrame);
                        }

                        //
                        // dodaj nowy jeśli istnieje tekst
                        //
                        if (!string.IsNullOrWhiteSpace(newText))
                        {
                            var frame =
                                TagLib.Id3v2.UserTextInformationFrame.Get(
                                    id3,
                                    "Napis",
                                    true);

                            frame.Text = new[] { newText };
                        }
                    }

                    //
                    // fallback do Comment
                    //
                    file.Tag.Comment = newText ?? "";

                    //
                    // SAVE
                    //
                    file.Save();
                }

                //
                // aktualizacja UI
                //
                item.Subtitle = newText;

                if (SubtitleText != null)
                {
                    SubtitleText.Text =
                        string.IsNullOrWhiteSpace(newText)
                        ? "Brak napisów"
                        : newText;
                }

                //
                // otwórz ponownie audio
                //
                mediaPlayer.Open(new Uri(item.FilePath));

                pendingSeek = currentPos;
                pendingResume = wasPlaying;

                System.Windows.MessageBox.Show(
                    "Napisy zapisane poprawnie.",
                    "OK",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    $"Nie można zapisać napisów:\n\n{ex.Message}",
                    "Błąd",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        

        private void btnsort1(object sender, RoutedEventArgs e)
        {
            if (chkSortByAlbum != null && chkSortByAlbum.IsChecked == true)
            {
                ReorderByAlbum(SortMode.AlphaAsc);
            }
            else
            {
                var ordered = audioFiles.OrderBy(a => a.Name).ToList();
                ReorderCollection(ordered);
            }
        }

        private void btnsort2(object sender, RoutedEventArgs e)
        {
            if (chkSortByAlbum != null && chkSortByAlbum.IsChecked == true)
            {
                ReorderByAlbum(SortMode.AlphaDesc);
            }
            else
            {
                var ordered = audioFiles.OrderByDescending(a => a.Name).ToList();
                ReorderCollection(ordered);
            }
        }

        private void btnsort3(object sender, RoutedEventArgs e)
        {
            if (chkSortByAlbum != null && chkSortByAlbum.IsChecked == true)
            {
                ReorderByAlbum(SortMode.Shuffle);
            }
            else
            {
                var list = audioFiles.ToList();
                int n = list.Count;
                while (n > 1)
                {
                    n--;
                    int k = rng.Next(n + 1);
                    var tmp = list[k];
                    list[k] = list[n];
                    list[n] = tmp;
                }
                ReorderCollection(list);
            }
        }

        private enum SortMode { AlphaAsc, AlphaDesc, Shuffle }

        private void ReorderByAlbum(SortMode mode)
        {
            // Build album groups in order: selected album first (if any), then others alphabetically. '-' or empty albums go to end.
            var primary = GetSelectedAlbum();

            var albums = audioFiles.Select(a => string.IsNullOrWhiteSpace(a.Album) ? "-" : a.Album)
                                    .Distinct()
                                    .ToList();

            // normalize primary (no-op removed)

            var orderedAlbums = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrWhiteSpace(primary) && albums.Contains(primary))
            {
                orderedAlbums.Add(primary);
            }

            var others = albums.Where(a => a != primary && a != "-")
                                .OrderBy(a => a, StringComparer.InvariantCultureIgnoreCase)
                                .ToList();
            orderedAlbums.AddRange(others);
            if (albums.Contains("-")) orderedAlbums.Add("-");

            var result = new System.Collections.Generic.List<AudioItem>();

            foreach (var alb in orderedAlbums)
            {
                var group = audioFiles.Where(a => (string.IsNullOrWhiteSpace(a.Album) ? "-" : a.Album) == alb).ToList();
                if (!group.Any()) continue;

                switch (mode)
                {
                    case SortMode.AlphaAsc:
                        group = group.OrderBy(a => a.Name).ToList();
                        break;
                    case SortMode.AlphaDesc:
                        group = group.OrderByDescending(a => a.Name).ToList();
                        break;
                    case SortMode.Shuffle:
                        int n = group.Count;
                        while (n > 1)
                        {
                            n--;
                            int k = rng.Next(n + 1);
                            var tmp = group[k];
                            group[k] = group[n];
                            group[n] = tmp;
                        }
                        break;
                }

                result.AddRange(group);
            }

            ReorderCollection(result);
        }

        private void ReorderCollection(System.Collections.Generic.IEnumerable<AudioItem> newOrder)
        {
            // dedupe by FilePath to avoid accidental duplicates
            var snapshot = newOrder.GroupBy(a => a.FilePath).Select(g => g.First()).ToList();
            audioFiles.Clear();
            foreach (var it in snapshot) audioFiles.Add(it);
        }

        
    }
}