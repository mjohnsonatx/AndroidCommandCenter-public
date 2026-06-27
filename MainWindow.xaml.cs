// MainWindow.xaml.cs
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace AndroidCommandCenter
{
    public partial class MainWindow : Window
    {
        private readonly ProcessRunner _runner = new();
        private readonly AndroidService _android;
        private CancellationTokenSource? _cts;

        public MainWindow()
        {
            InitializeComponent();
            _android = new AndroidService(_runner);
            // Marshal output back to the UI thread
            _runner.OutputReceived += line =>
                Dispatcher.Invoke(() => AppendLog(line));
        }

        private void AppendLog(string line)
        {
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog(); // .NET 8 has this built in
            if (dlg.ShowDialog() == true)
            {
                _android.ProjectPath = dlg.FolderName;
                ProjectBox.Text = dlg.FolderName;
            }
        }

        private async Task Run(Func<CancellationToken, Task<int>> op, string name)
        {
            if (string.IsNullOrEmpty(_android.ProjectPath))
            {
                AppendLog("[!] Select a project folder first.");
                return;
            }
            _cts = new CancellationTokenSource();
            AppendLog($"\n===== {name} started =====");
            try
            {
                int code = await op(_cts.Token);
                AppendLog($"===== {name} finished (exit {code}) =====");
            }
            catch (OperationCanceledException)
            {
                AppendLog($"===== {name} cancelled =====");
            }
            catch (Exception ex)
            {
                AppendLog($"[ERROR] {ex.Message}");
            }
        }

        private async void Clean_Click(object s, RoutedEventArgs e) => await Run(_android.CleanBuild, "Clean Build");
        private async void UnitTests_Click(object s, RoutedEventArgs e) => await Run(_android.UnitTests, "Unit Tests");
        private async void Instrument_Click(object s, RoutedEventArgs e) => await Run(_android.InstrumentTests, "Instrument Tests");
        private async void StopDaemons_Click(object s, RoutedEventArgs e) => await Run(_android.StopDaemons, "Stop Daemons");
        private async void Devices_Click(object s, RoutedEventArgs e) => await Run(_android.ListDevices, "List Devices");
        private void Cancel_Click(object s, RoutedEventArgs e) => _cts?.Cancel();
    }
}