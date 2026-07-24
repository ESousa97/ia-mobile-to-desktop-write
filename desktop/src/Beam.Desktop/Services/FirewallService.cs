using System.Diagnostics;
using System.Reflection;

namespace Beam.Desktop.Services;

/// <summary>
/// Regras de entrada do Firewall do Windows necessárias para o celular alcançar
/// o desktop pela Wi-Fi.
/// </summary>
/// <remarks>
/// Sem elas o servidor sobe normalmente, mas o Windows descarta silenciosamente
/// a conexão do celular — sobretudo em redes classificadas como "Pública", o
/// padrão da maioria das Wi-Fi. Criar regra exige elevação, então a aplicação
/// nunca a cria sozinha: apenas verifica e, sob clique explícito do usuário,
/// dispara o <c>netsh</c> com o prompt do UAC.
/// </remarks>
public static class FirewallService
{
    private static string TcpRuleName(int port) => $"Beam (TCP {port})";

    private static string UdpRuleName(int port) => $"Beam descoberta (UDP {port})";

    private const string ProgramRuleName = "Beam (programa)";

    /// <summary>Regras de entrada que bloqueiam o executável do Beam.</summary>
    private const string BlockedRulesPipeline =
        "Get-NetFirewallApplicationFilter -All -ErrorAction SilentlyContinue | " +
        "Where-Object Program -like '*\\Beam.exe' | " +
        "Get-NetFirewallRule -ErrorAction SilentlyContinue | " +
        "Where-Object Direction -eq Inbound | " +
        "Where-Object Action -eq Block | " +
        "Where-Object Enabled -eq True";

    /// <summary>Caminho do executável em execução, usado na regra por programa.</summary>
    private static string ExecutablePath =>
        Environment.ProcessPath ?? Assembly.GetEntryAssembly()?.Location ?? string.Empty;

    /// <summary>
    /// Indica se existe alguma regra de <b>bloqueio</b> de entrada apontando para o
    /// executável do Beam.
    /// </summary>
    /// <remarks>
    /// No Firewall do Windows, bloqueio tem precedência sobre permissão: basta uma
    /// dessas regras para a conexão do celular ser descartada em silêncio, por mais
    /// regras de porta que se acrescente. Elas aparecem sozinhas quando o usuário
    /// recusa (ou fecha) o aviso "Permitir acesso?" na primeira execução.
    /// </remarks>
    public static bool HasBlockingRule()
    {
        var executable = ExecutablePath;
        if (string.IsNullOrEmpty(executable))
        {
            return false;
        }

        // Sem `$_` de propósito: a string ainda passa por cmd/PowerShell externos ao
        // ser elevada, e a variável seria consumida antes de chegar ao cmdlet.
        var output = RunCaptured(
            "powershell",
            $"-NoProfile -NonInteractive -Command \"@({BlockedRulesPipeline}).Count\"");

        return int.TryParse(output?.Trim(), out var count) && count > 0;
    }

    /// <summary>Comando equivalente, para o usuário aplicar manualmente num terminal elevado.</summary>
    public static string ManualCommand(int tcpPort, int discoveryPort, int announcePort) =>
        $"netsh advfirewall firewall add rule name=\"{TcpRuleName(tcpPort)}\" dir=in action=allow protocol=TCP localport={tcpPort} profile=any & " +
        $"netsh advfirewall firewall add rule name=\"{UdpRuleName(discoveryPort)}\" dir=in action=allow protocol=UDP localport={discoveryPort},{announcePort} profile=any";

    /// <summary>
    /// Indica se o caminho de entrada está liberado: as regras existem e nenhuma
    /// regra de bloqueio do executável as anula.
    /// </summary>
    public static bool AreRulesPresent(int tcpPort, int discoveryPort) =>
        RuleExists(TcpRuleName(tcpPort)) && RuleExists(UdpRuleName(discoveryPort)) && !HasBlockingRule();

    /// <summary>
    /// Pede ao Windows para criar as regras, com prompt do UAC. Retorna falso se o
    /// usuário recusar a elevação.
    /// </summary>
    public static bool TryCreateRules(int tcpPort, int discoveryPort, int announcePort)
    {
        var tcpRule = TcpRuleName(tcpPort);
        var udpRule = UdpRuleName(discoveryPort);
        var executable = ExecutablePath;

        // 1) Apaga regras de bloqueio do próprio executável — enquanto existirem,
        //    o Windows descarta a conexão do celular por mais que se permita a porta.
        // Dentro do cmd os pipes precisam ser escapados com ^.
        var removeBlocks =
            "powershell -NoProfile -NonInteractive -Command \"" +
            BlockedRulesPipeline.Replace("|", "^|") +
            " ^| Remove-NetFirewallRule\"";

        // 2) Recria as permissões. Delete antes de add: reexecutar não empilha duplicatas.
        var command =
            $"/c {removeBlocks} >nul 2>&1 & " +
            $"netsh advfirewall firewall delete rule name=\"{tcpRule}\" >nul 2>&1 & " +
            $"netsh advfirewall firewall delete rule name=\"{udpRule}\" >nul 2>&1 & " +
            $"netsh advfirewall firewall delete rule name=\"{ProgramRuleName}\" >nul 2>&1 & " +
            $"netsh advfirewall firewall add rule name=\"{tcpRule}\" dir=in action=allow protocol=TCP localport={tcpPort} profile=any & " +
            $"netsh advfirewall firewall add rule name=\"{udpRule}\" dir=in action=allow protocol=UDP localport={discoveryPort},{announcePort} profile=any";

        // 3) Regra por programa: cobre o caso de o executável mudar de porta e é a
        //    forma que o Windows propõe no aviso da primeira execução.
        if (!string.IsNullOrEmpty(executable))
        {
            command += $" & netsh advfirewall firewall add rule name=\"{ProgramRuleName}\" dir=in action=allow program=\"{executable}\" enable=yes profile=any";
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", command)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });

            if (process is null)
            {
                return false;
            }

            // Consultar regras por programa passa pelo PowerShell e pode demorar
            // bem mais que o netsh puro.
            process.WaitForExit(60_000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch (Exception)
        {
            // Usuário recusou o UAC ou a política do sistema bloqueia a elevação.
            return false;
        }
    }

    private static bool RuleExists(string name) =>
        RunCaptured("netsh", $"advfirewall firewall show rule name=\"{name}\"") is not null;

    /// <summary>
    /// Executa um comando de leitura sem elevação e devolve a saída, ou null se o
    /// processo falhar. Nunca abre janela.
    /// </summary>
    private static string? RunCaptured(string fileName, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(30_000);
            return process.HasExited && process.ExitCode == 0 ? output : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
