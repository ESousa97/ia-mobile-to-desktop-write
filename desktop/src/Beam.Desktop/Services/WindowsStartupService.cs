using Microsoft.Win32;

namespace Beam.Desktop.Services;

/// <summary>Registra o Beam para iniciar com o Windows no perfil do usuário.</summary>
public static class WindowsStartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Beam";

    public static void EnsureRegistered()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return;
        }

        using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        runKey?.SetValue(ValueName, $"\"{processPath}\"");
    }
}