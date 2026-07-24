using System.Security.Cryptography;
using System.Text.Json;

namespace Beam.Core.Security;

/// <summary>Vínculo persistido com um celular já pareado.</summary>
/// <param name="DeviceId">Identificador derivado da chave de retomada.</param>
/// <param name="ResumeKey">Chave de retomada (32 bytes).</param>
/// <param name="ExpiresAt">Instante em que a confiança caduca.</param>
public sealed record TrustedDevice(string DeviceId, byte[] ResumeKey, DateTimeOffset ExpiresAt);

/// <summary>
/// Protege segredos em repouso. A implementação concreta depende da plataforma
/// (no Windows, DPAPI com escopo do usuário atual).
/// </summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] plaintext);

    byte[] Unprotect(byte[] ciphertext);
}

/// <summary>Registro dos celulares confiáveis e da validade de cada vínculo.</summary>
public interface ITrustedDeviceStore
{
    /// <summary>Devolve o vínculo se existir e ainda estiver válido; senão, null.</summary>
    TrustedDevice? Find(string deviceId);

    /// <summary>Cria ou renova um vínculo, com validade a partir de agora.</summary>
    void Remember(string deviceId, byte[] resumeKey);

    /// <summary>Estende a validade após uma conexão bem-sucedida.</summary>
    void Renew(string deviceId);

    /// <summary>Esquece o vínculo (revogação explícita ou retomada rejeitada).</summary>
    void Forget(string deviceId);

    /// <summary>Revoga todos os vínculos: nenhuma retomada é aceita até um novo pareamento.</summary>
    void ForgetAll();

    /// <summary>Quantidade de vínculos válidos no momento.</summary>
    int Count { get; }
}

/// <summary>
/// Implementação em arquivo único, com o conteúdo cifrado pelo
/// <see cref="ISecretProtector"/> da plataforma.
/// </summary>
/// <remarks>
/// O arquivo guarda chaves de retomada: quem as obtém consegue reconectar sem o
/// código de pareamento até a expiração. Por isso nunca é gravado em texto claro,
/// e entradas vencidas são descartadas na leitura — o vínculo expira mesmo que o
/// relógio ande para trás entre execuções.
/// </remarks>
public sealed class FileTrustedDeviceStore : ITrustedDeviceStore
{
    private readonly string _path;
    private readonly ISecretProtector _protector;
    private readonly object _gate = new();
    private Dictionary<string, TrustedDevice>? _devices;

    public FileTrustedDeviceStore(string path, ISecretProtector protector)
    {
        _path = path;
        _protector = protector;
    }

    /// <summary>Caminho padrão: <c>%LOCALAPPDATA%\Beam\trusted-devices.bin</c>.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Beam",
        "trusted-devices.bin");

    public TrustedDevice? Find(string deviceId)
    {
        lock (_gate)
        {
            var devices = Load();
            if (!devices.TryGetValue(deviceId, out var device))
            {
                return null;
            }

            if (device.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                devices.Remove(deviceId);
                Save(devices);
                return null;
            }

            return device;
        }
    }

    public void Remember(string deviceId, byte[] resumeKey)
    {
        lock (_gate)
        {
            var devices = Load();
            devices[deviceId] = new TrustedDevice(
                deviceId,
                resumeKey.ToArray(),
                DateTimeOffset.UtcNow.Add(ResumeHandshake.TrustLifetime));
            Save(devices);
        }
    }

    public void Renew(string deviceId)
    {
        lock (_gate)
        {
            var devices = Load();
            if (!devices.TryGetValue(deviceId, out var device))
            {
                return;
            }

            devices[deviceId] = device with
            {
                ExpiresAt = DateTimeOffset.UtcNow.Add(ResumeHandshake.TrustLifetime),
            };
            Save(devices);
        }
    }

    public void Forget(string deviceId)
    {
        lock (_gate)
        {
            var devices = Load();
            if (devices.Remove(deviceId))
            {
                Save(devices);
            }
        }
    }

    public void ForgetAll()
    {
        lock (_gate)
        {
            var devices = Load();
            devices.Clear();
            Save(devices);
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return Load().Count(entry => entry.Value.ExpiresAt > DateTimeOffset.UtcNow);
            }
        }
    }

    private Dictionary<string, TrustedDevice> Load()
    {
        if (_devices is not null)
        {
            return _devices;
        }

        _devices = new Dictionary<string, TrustedDevice>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(_path))
            {
                return _devices;
            }

            var plaintext = _protector.Unprotect(File.ReadAllBytes(_path));
            var entries = JsonSerializer.Deserialize<List<PersistedDevice>>(plaintext) ?? [];
            foreach (var entry in entries)
            {
                if (entry.DeviceId is null || entry.ResumeKey is null ||
                    entry.ExpiresAt <= DateTimeOffset.UtcNow)
                {
                    continue;
                }

                _devices[entry.DeviceId] = new TrustedDevice(
                    entry.DeviceId,
                    Convert.FromBase64String(entry.ResumeKey),
                    entry.ExpiresAt);
            }
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or JsonException or FormatException)
        {
            // Arquivo corrompido, de outro usuário do Windows ou de outra máquina:
            // trata como "sem vínculos" — o pareamento por código sempre funciona.
            _devices.Clear();
        }

        return _devices;
    }

    private void Save(Dictionary<string, TrustedDevice> devices)
    {
        try
        {
            var entries = devices.Values
                .Select(device => new PersistedDevice(
                    device.DeviceId,
                    Convert.ToBase64String(device.ResumeKey),
                    device.ExpiresAt))
                .ToList();

            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(_path, _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(entries)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            // Sem persistência o app segue funcionando; só perde a reconexão
            // automática entre execuções.
        }
    }

    private sealed record PersistedDevice(string? DeviceId, string? ResumeKey, DateTimeOffset ExpiresAt);
}
