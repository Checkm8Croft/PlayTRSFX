using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Media;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace PlayTRSFX
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private byte[] _fileData = Array.Empty<byte>();
        private List<int> _offsets = new List<int>();
        private SoundPlayer _player = new SoundPlayer();

        public MainWindow()
        {
            InitializeComponent();
            // UI simplified: no bits/channels controls
            TryApplyWindowsTheme();

            // handle preview key so 'P' triggers play
            this.PreviewKeyDown += MainWindow_PreviewKeyDown;
        }

        private void MainWindow_PreviewKeyDown(object? sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e == null) return;
            if (e.Key == System.Windows.Input.Key.P)
            {
                PlaySelected();
                e.Handled = true;
            }
        }

        private void TryApplyWindowsTheme()
        {
            try
            {
                // AppsUseLightTheme = 1 -> light, 0 -> dark
                var key = "HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize";
                var v = Microsoft.Win32.Registry.GetValue(key, "AppsUseLightTheme", 1);
                if (v is int iv && iv == 0)
                {
                    // apply simple dark theme
                    var darkBg = (System.Windows.Media.Brush)new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 30, 30));
                    var darkPanel = (System.Windows.Media.Brush)new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 40, 40));
                    var lightFg = (System.Windows.Media.Brush)new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(240, 240, 240));

                    this.Background = darkBg;
                    this.Foreground = lightFg;

                    OpenButton.Background = darkPanel;
                    OpenButton.Foreground = lightFg;
                    PlayButton.Background = darkPanel;
                    PlayButton.Foreground = lightFg;

                    // ensure title is white and larger in dark mode (resolve by name at runtime)
                    var titleLbl = this.FindName("TitleLabel") as Label;
                    if (titleLbl != null) titleLbl.Foreground = lightFg;

                    SoundsList.Background = darkPanel;
                    SoundsList.Foreground = lightFg;
                    DetailsBox.Background = darkPanel;
                    DetailsBox.Foreground = lightFg;
                }
            }
            catch
            {
                // ignore failures and keep default theme
            }
        }

        private void OpenButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog();
            dlg.Filter = "main.sfx files|*.sfx|All files|*.*";
            if (dlg.ShowDialog() != true) return;

            try
            {
                _fileData = File.ReadAllBytes(dlg.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to read file: " + ex.Message);
                return;
            }

            _offsets = TryParseOffsets(_fileData);

            SoundsList.Items.Clear();
            if (_offsets.Count >= 2)
            {
                for (int i = 0; i < _offsets.Count - 1; i++)
                {
                    int start = _offsets[i];
                    int len = _offsets[i + 1] - start;
                    SoundsList.Items.Add($"Sound {i}: offset={start} len={len}");
                }
            }
            else
            {
                // single entry
                SoundsList.Items.Add($"Sound 0: offset=0 len={_fileData.Length}");
                _offsets = new List<int> { 0, _fileData.Length };
            }

            DetailsBox.Text = DescribeOffsets(_offsets, _fileData.Length);
        }

        private void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            PlaySelected();
        }

        private void SoundsList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            PlaySelected();
        }

        private void PlaySelected()
        {
            if (_fileData == null || _offsets == null || _offsets.Count < 2)
            {
                MessageBox.Show("Open a main.sfx file first.");
                return;
            }

            int idx = SoundsList.SelectedIndex;
            if (idx < 0) idx = 0;
            if (idx >= _offsets.Count - 1) idx = _offsets.Count - 2;

            int start = _offsets[idx];
            int end = _offsets[idx + 1];
            int len = Math.Max(0, end - start);

            // simplified: use sensible defaults; prefer playing embedded WAV chunks
            var sampleRate = 22050;
            int bits = 8;
            int channels = 1;
            bool signed8 = false;

            byte[] raw = new byte[len];
            Array.Copy(_fileData, start, raw, 0, len);

            // If this segment already contains a RIFF/WAVE chunk, play it directly
            bool isWav = raw.Length >= 12 && raw[0] == (byte)'R' && raw[1] == (byte)'I' && raw[2] == (byte)'F' && raw[3] == (byte)'F'
                         && raw[8] == (byte)'W' && raw[9] == (byte)'A' && raw[10] == (byte)'V' && raw[11] == (byte)'E';

            try
            {
                if (isWav)
                {
                    _player.Stop();
                    _player.Stream = new MemoryStream(raw);
                    _player.Load();
                    _player.Play();
                    return;
                }

                // auto-detect signedness for raw samples
                signed8 = DetectSigned8(raw);
                var wav = BuildWavFromRaw(raw, sampleRate, bits, channels, signed8);
                _player.Stop();
                _player.Stream = new MemoryStream(wav);
                _player.Load();
                _player.Play();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to play: " + ex.Message);
            }
        }

        private static string DescribeOffsets(List<int> offsets, int fileLength)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"File length: {fileLength} bytes");
            sb.AppendLine($"Detected entries: {Math.Max(0, offsets.Count - 1)}");
            for (int i = 0; i < offsets.Count - 1; i++)
            {
                int s = offsets[i];
                int e = offsets[i + 1];
                sb.AppendLine($"[{i}] offset={s} len={e - s}");
            }
            return sb.ToString();
        }

        private static List<int> TryParseOffsets(byte[] data)
        {
            int fileSize = data.Length;

            // First attempt: detect concatenated WAV files (RIFF/WAVE chunks)
            var wavOffsets = ParseWavOffsets(data);
            if (wavOffsets != null && wavOffsets.Count >= 2)
            {
                return NormalizeOffsets(wavOffsets, fileSize);
            }

            int bestScore = 0;
            List<int>? bestOffsets = null;

            var elemSizes = new[] { 4, 2 };
            int maxStart = Math.Min(4096, fileSize / 4);
            foreach (var elemSize in elemSizes)
            {
                for (int start = 0; start <= maxStart; start += elemSize)
                {
                    int available = fileSize - start;
                    if (available < elemSize * 2) break;
                    int maxEntries = Math.Min(10000, available / elemSize);
                    var list = new List<int>();
                    for (int i = 0; i < maxEntries; i++)
                    {
                        int idx = start + i * elemSize;
                        if (idx + elemSize > fileSize) break;
                        int offset = elemSize == 4 ? (int)BitConverter.ToUInt32(data, idx) : (int)BitConverter.ToUInt16(data, idx);
                        if (offset < 0 || offset > fileSize) break;
                        list.Add(offset);
                        if (i > 0 && list[i] < list[i - 1]) break; // non-monotonic
                    }

                    if (list.Count < 2) continue;

                    // Evaluate two hypotheses: offsets are absolute, or offsets are relative to table end
                    var candidates = new List<List<int>>();

                    // absolute
                    var abs = new List<int>(list);
                    if (abs[abs.Count - 1] < fileSize) abs.Add(fileSize);
                    candidates.Add(abs);

                    // relative to table end
                    long tableSize = (long)list.Count * elemSize;
                    var rel = list.Select(v => v + (int)tableSize).ToList();
                    if (rel[rel.Count - 1] < fileSize) rel.Add(fileSize);
                    candidates.Add(rel);

                    foreach (var cand in candidates)
                    {
                        if (!IsMonotonic(cand)) continue;
                        int goodLenCount = 0;
                        int totalLen = 0;
                        for (int i = 0; i < cand.Count - 1; i++)
                        {
                            int l = cand[i + 1] - cand[i];
                            totalLen += Math.Max(0, l);
                            if (l >= 16) goodLenCount++;
                        }
                        // score favors many entries with reasonable length
                        int score = goodLenCount * cand.Count + Math.Min(totalLen / 1024, 1000);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestOffsets = cand;
                        }
                    }
                }
            }

            if (bestOffsets != null && bestOffsets.Count >= 2)
            {
                return NormalizeOffsets(bestOffsets, fileSize);
            }

            // fallback: single entry covering whole file
            return new List<int> { 0, fileSize };
        }

        private static List<int>? ParseWavOffsets(byte[] data)
        {
            var offs = new List<int>();
            int i = 0;
            int n = data.Length;
            while (i + 12 <= n)
            {
                // look for 'RIFF'
                if (data[i] == (byte)'R' && data[i + 1] == (byte)'I' && data[i + 2] == (byte)'F' && data[i + 3] == (byte)'F')
                {
                    // read chunk size (little endian)
                    if (i + 8 > n) break;
                    uint chunkSize = BitConverter.ToUInt32(data, i + 4);
                    // verify 'WAVE' id
                    if (i + 8 + 4 <= n && data[i + 8] == (byte)'W' && data[i + 9] == (byte)'A' && data[i + 10] == (byte)'V' && data[i + 11] == (byte)'E')
                    {
                        long total = 8 + chunkSize; // RIFF header + chunkSize
                        if (total <= 0 || i + total > n) break; // invalid or truncated
                        offs.Add(i);
                        i += (int)total;
                        continue;
                    }
                }
                i++;
            }
            if (offs.Count == 0) return null;
            // ensure we add file end
            if (offs[0] != 0) offs.Insert(0, 0); // keep original offset 0 as safety
            // build list with end markers
            var result = new List<int>();
            foreach (var o in offs) result.Add(o);
            // append file end if last riff didn't exactly end at EOF
            if (result[result.Count - 1] != n) result.Add(n);
            // remove duplicates and sort
            return result.Distinct().OrderBy(x => x).ToList();
        }

        private static List<int> NormalizeOffsets(List<int> offs, int fileSize)
        {
            var result = offs.Distinct().OrderBy(x => x).ToList();
            // ensure last entry equals fileSize
            if (result[result.Count - 1] != fileSize)
            {
                result.Add(fileSize);
            }
            return result;
        }

        private static bool IsMonotonic(List<int> list)
        {
            for (int i = 1; i < list.Count; i++) if (list[i] < list[i - 1]) return false;
            return true;
        }

        private static bool DetectSigned8(byte[] raw)
        {
            if (raw == null || raw.Length == 0) return false;
            int take = Math.Min(raw.Length, 4096);
            var hist = new int[256];
            for (int i = 0; i < take; i++) hist[raw[i]]++;
            int mode = 0;
            for (int i = 1; i < 256; i++) if (hist[i] > hist[mode]) mode = i;
            // if mode is near 128, it's likely unsigned (silence at 128)
            if (mode >= 120 && mode <= 136) return false; // unsigned
            // if mode is near 0 or 255, likely signed (silence at 0)
            if (mode <= 8 || mode >= 248) return true; // signed
            // fallback: treat as signed if mode < 64
            return mode < 64;
        }

        private static byte[] BuildWavFromRaw(byte[] raw, int sampleRate, int bitsPerSample, int channels, bool signed8)
        {
            // Convert raw 8-bit samples to requested output format
            // Assume input raw is 8-bit samples (signed or unsigned). Support expanding to 16-bit and duplicating channels.

            byte[] pcm;
            if (bitsPerSample == 8)
            {
                // output one byte per sample per channel
                pcm = new byte[raw.Length * channels];
                for (int i = 0; i < raw.Length; i++)
                {
                    byte b = raw[i];
                    if (signed8)
                    {
                        // convert signed byte to unsigned PCM (add 128)
                        unchecked { b = (byte)((sbyte)b + 128); }
                    }
                    for (int c = 0; c < channels; c++) pcm[i * channels + c] = b;
                }
            }
            else if (bitsPerSample == 16)
            {
                pcm = new byte[raw.Length * 2 * channels];
                for (int i = 0; i < raw.Length; i++)
                {
                    byte b = raw[i];
                    short s;
                    if (signed8)
                    {
                        s = (short)((sbyte)b << 8); // signed 8 -> signed 16
                    }
                    else
                    {
                        // unsigned 8 -> signed 16 centered
                        s = (short)((b - 128) << 8);
                    }
                    // write little-endian
                    byte lo = (byte)(s & 0xFF);
                    byte hi = (byte)((s >> 8) & 0xFF);
                    for (int c = 0; c < channels; c++)
                    {
                        int baseIndex = (i * channels + c) * 2;
                        pcm[baseIndex] = lo;
                        pcm[baseIndex + 1] = hi;
                    }
                }
            }
            else
            {
                throw new ArgumentException("Unsupported bits per sample");
            }

            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms, Encoding.ASCII, true))
            {
                int byteRate = sampleRate * channels * bitsPerSample / 8;
                int blockAlign = channels * bitsPerSample / 8;

                // RIFF header
                bw.Write(Encoding.ASCII.GetBytes("RIFF"));
                bw.Write(36 + pcm.Length); // file size - 8
                bw.Write(Encoding.ASCII.GetBytes("WAVE"));

                // fmt chunk
                bw.Write(Encoding.ASCII.GetBytes("fmt "));
                bw.Write(16); // fmt chunk size
                bw.Write((short)1); // PCM
                bw.Write((short)channels);
                bw.Write(sampleRate);
                bw.Write(byteRate);
                bw.Write((short)blockAlign);
                bw.Write((short)bitsPerSample);

                // data chunk
                bw.Write(Encoding.ASCII.GetBytes("data"));
                bw.Write(pcm.Length);
                bw.Write(pcm);

                bw.Flush();
                return ms.ToArray();
            }
        }
    }
}