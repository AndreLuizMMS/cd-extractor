namespace MediaRescue.Core;

/// <summary>
/// RF-03 destination checks. Blocking issues must disable "Extrair"; warnings do not. When nothing is wrong a single
/// <see cref="ValidationSeverity.Ok"/> item is returned (so callers can always show a status line).
/// File-level blocks (FAT32 / 4 GB) carry the offending <see cref="ValidationIssue.RelativePath"/> so the UI can
/// un-check just those files (E-17).
/// </summary>
public sealed class DestinationValidator : IDestinationValidator
{
    public const long Fat32MaxFileBytes = 4L * 1024 * 1024 * 1024 - 1;
    public const long Fat16MaxFileBytes = 2L * 1024 * 1024 * 1024 - 1;
    public const long ExtraBytes = 50L * 1024 * 1024;
    public const long LowSystemDriveFreeBytes = 10L * 1024 * 1024 * 1024;
    public const int RecommendedMaxPathLength = 240;

    private readonly IDestinationFileSystem _fs;

    public DestinationValidator(IDestinationFileSystem fs) => _fs = fs ?? throw new ArgumentNullException(nameof(fs));

    /// <summary>Bytes needed for a copy: selected bytes + 5% + 50 MB (RF-03).</summary>
    public static long RequiredBytes(long selectedBytes) => (long)Math.Ceiling(selectedBytes * 1.05) + ExtraBytes;

    public IReadOnlyList<ValidationIssue> Validate(string destinationRoot, string folderName, IReadOnlyList<DiscEntry> entries, string opticalDriveLetter)
    {
        var issues = new List<ValidationIssue>();
        entries ??= Array.Empty<DiscEntry>();

        if (string.IsNullOrWhiteSpace(destinationRoot))
            return Block("Escolha uma pasta de destino.");
        if (string.IsNullOrWhiteSpace(folderName))
            issues.Add(new(ValidationSeverity.Blocking, "Informe um nome para a pasta do disco."));

        DestinationInfo? info;
        bool exists;
        try
        {
            exists = _fs.DirectoryExists(destinationRoot);
            info = exists ? _fs.GetInfo(destinationRoot) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Block($"Não foi possível acessar o destino: {ex.Message}");
        }
        if (!exists)
            return Block("A pasta de destino não existe ou não está disponível. Escolha outra pasta ou reconecte o pendrive/HD.");

        // The destination must not be the optical drive itself (E: reading from and writing to the same place).
        var destLetter = DriveLetterOf(destinationRoot) ?? DriveLetterOf(info?.Root);
        var optLetter = DriveLetterOf(opticalDriveLetter);
        if ((info?.IsOptical ?? false) || (destLetter != null && optLetter != null && destLetter == optLetter))
            return Block("O destino não pode ser o próprio leitor de CD/DVD. Escolha uma pasta no computador, pendrive ou HD externo.");

        if (!_fs.ProbeWritable(destinationRoot, out var writeError))
            return Block("Não é possível gravar nesta pasta (somente leitura, sem permissão ou protegida contra gravação)."
                         + (string.IsNullOrWhiteSpace(writeError) ? "" : $" Detalhe: {writeError}"));

        var files = entries.Where(e => !e.IsDirectory).ToList();
        long selected = files.Sum(e => Math.Max(0, e.Size));

        if (info != null)
        {
            long required = RequiredBytes(selected);
            if (info.FreeBytes < required)
                issues.Add(new(ValidationSeverity.Blocking,
                    $"Não há espaço suficiente no destino: faltam {Fmt.Bytes(required - info.FreeBytes)} " +
                    $"(necessário {Fmt.Bytes(required)} com margem de segurança, livre {Fmt.Bytes(info.FreeBytes)}). Libere espaço ou escolha outro destino."));

            long? limit = FileSizeLimit(info.FileSystem);
            if (limit != null)
                foreach (var f in files.Where(f => f.Size > limit.Value))
                    issues.Add(new(ValidationSeverity.Blocking,
                        $"O arquivo \"{f.RelativePath}\" ({Fmt.Bytes(f.Size)}) é grande demais para o formato {info.FileSystem.ToUpperInvariant()} do destino " +
                        $"(limite de {Fmt.Bytes(limit.Value + 1)} por arquivo). Formate o pendrive em exFAT ou escolha outro destino.", f.RelativePath));

            if (string.Equals(destLetter, "C:", StringComparison.Ordinal) && info.FreeBytes - selected < LowSystemDriveFreeBytes)
                issues.Add(new(ValidationSeverity.Warning,
                    $"Depois da cópia o disco C: ficará com menos de 10 GB livres (restariam cerca de {Fmt.Bytes(Math.Max(0, info.FreeBytes - selected))}). Isso pode deixar o computador lento."));
        }
        else
        {
            issues.Add(new(ValidationSeverity.Warning, "Não foi possível verificar o espaço livre do destino."));
        }

        if ((info?.IsNetworkOrCloud ?? false) || destinationRoot.StartsWith(@"\\", StringComparison.Ordinal))
            issues.Add(new(ValidationSeverity.Warning,
                "O destino é uma unidade de rede ou de nuvem. A cópia usa um cache no disco C: e pode falhar por falta de espaço ou de conexão."));

        if (!string.IsNullOrWhiteSpace(folderName) && _fs.DirectoryExists(Path.Combine(destinationRoot, folderName)))
        {
            var suggestion = NameSanitizer.MakeUnique(folderName, n => _fs.DirectoryExists(Path.Combine(destinationRoot, n)));
            issues.Add(new(ValidationSeverity.Warning,
                $"Já existe uma pasta chamada \"{folderName}\" no destino. Nada será sobrescrito; considere usar \"{suggestion}\"."));
        }

        if (!string.IsNullOrWhiteSpace(folderName))
        {
            var map = PathMap.Build(entries.Select(e => e.RelativePath).Where(p => !string.IsNullOrWhiteSpace(p)));
            var prefix = Path.Combine(destinationRoot, folderName).Length + 1;
            int tooLong = map.Values.Count(w => prefix + w.Length > RecommendedMaxPathLength);
            if (tooLong > 0)
                issues.Add(new(ValidationSeverity.Warning,
                    $"{tooLong} arquivo(s) ficarão com caminho maior que {RecommendedMaxPathLength} caracteres; alguns programas podem não conseguir abri-los. Use uma pasta de destino mais curta."));
        }

        if (issues.Count == 0)
        {
            var label = string.IsNullOrWhiteSpace(info?.VolumeLabel) ? destinationRoot : info!.VolumeLabel;
            var fsName = string.IsNullOrWhiteSpace(info?.FileSystem) ? "" : $" ({info!.FileSystem})";
            var free = info == null ? "" : $" — {Fmt.Bytes(info.FreeBytes)} livres";
            issues.Add(new(ValidationSeverity.Ok, $"{label}{fsName}{free}"));
        }
        return issues;
    }

    private static IReadOnlyList<ValidationIssue> Block(string message) => new[] { new ValidationIssue(ValidationSeverity.Blocking, message) };

    private static long? FileSizeLimit(string? fileSystem)
    {
        if (string.Equals(fileSystem, "FAT32", StringComparison.OrdinalIgnoreCase)) return Fat32MaxFileBytes;
        if (string.Equals(fileSystem, "FAT", StringComparison.OrdinalIgnoreCase) || string.Equals(fileSystem, "FAT16", StringComparison.OrdinalIgnoreCase))
            return Fat16MaxFileBytes;
        return null;
    }

    /// <summary>"D:" for "D:", "d:\", "D:\Videos" or "D"; null for UNC/relative paths.</summary>
    internal static string? DriveLetterOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = path.Trim();
        if (path.Length == 1 && char.IsLetter(path[0])) return char.ToUpperInvariant(path[0]) + ":";
        if (path.Length >= 2 && path[1] == ':' && char.IsLetter(path[0])) return char.ToUpperInvariant(path[0]) + ":";
        return null;
    }
}
