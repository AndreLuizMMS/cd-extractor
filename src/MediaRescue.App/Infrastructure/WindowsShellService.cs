using System.Diagnostics;
using System.Media;
using System.Text;
using System.Windows;
using Microsoft.Win32;

namespace MediaRescue.App.Infrastructure;

/// <summary>Real implementation of <see cref="IShellService"/> (Explorer, file dialogs, clipboard, system sounds).</summary>
public sealed class WindowsShellService : IShellService
{
    private static Window? Owner => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;

    public bool OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            return true;
        }
        catch (Exception) { return false; }
    }

    public string? PickFolder(string? initialPath)
    {
        var dlg = new OpenFolderDialog { Title = "Escolha a pasta de destino", Multiselect = false };
        if (!string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath)) dlg.InitialDirectory = initialPath;
        var ok = Owner != null ? dlg.ShowDialog(Owner) : dlg.ShowDialog();
        return ok == true ? dlg.FolderName : null;
    }

    public string? PickSaveFile(string suggestedName, string filter)
    {
        var dlg = new SaveFileDialog { FileName = suggestedName, Filter = filter, AddExtension = true, OverwritePrompt = true, Title = "Salvar como" };
        var ok = Owner != null ? dlg.ShowDialog(Owner) : dlg.ShowDialog();
        return ok == true ? dlg.FileName : null;
    }

    // UTF-8 with BOM so Excel opens accented characters correctly.
    public Task WriteAllTextAsync(string path, string text) => File.WriteAllTextAsync(path, text, new UTF8Encoding(true));

    public void CopyToClipboard(string text)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try { Clipboard.SetDataObject(text, true); return; }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(60); } // clipboard briefly locked by another app
        }
    }

    public void PlaySound(SoundKind kind)
    {
        try
        {
            switch (kind)
            {
                case SoundKind.Success: SystemSounds.Asterisk.Play(); break;
                case SoundKind.Warning: SystemSounds.Exclamation.Play(); break;
                default: SystemSounds.Hand.Play(); break;
            }
        }
        catch (Exception) { /* no audio device */ }
    }

    public void OpenLog()
    {
        try
        {
            var file = AppLogging.LatestLogFile();
            Process.Start(new ProcessStartInfo(file ?? AppLogging.LogDirectory) { UseShellExecute = true });
        }
        catch (Exception) { /* nothing sensible to do */ }
    }
}
