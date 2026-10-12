using MediaRescue.Core;

namespace MediaRescue.Windows.Native;

/// <summary>Win32 error classification (SPEC RF-05) and user-facing messages.</summary>
internal static class Win32Errors
{
    public const int ERROR_FILE_NOT_FOUND = 2;
    public const int ERROR_PATH_NOT_FOUND = 3;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_INVALID_HANDLE = 6;
    public const int ERROR_NOT_READY = 21;
    public const int ERROR_CRC = 23;
    public const int ERROR_SECTOR_NOT_FOUND = 27;
    public const int ERROR_SHARING_VIOLATION = 32;
    public const int ERROR_HANDLE_EOF = 38;
    public const int ERROR_INVALID_PARAMETER = 87;
    public const int ERROR_SEM_TIMEOUT = 121;
    public const int ERROR_OPERATION_ABORTED = 995;
    public const int ERROR_IO_PENDING = 997;
    public const int ERROR_UNRECOGNIZED_VOLUME = 1005;
    public const int ERROR_MEDIA_CHANGED = 1110;
    public const int ERROR_NO_MEDIA_IN_DRIVE = 1112;
    public const int ERROR_IO_DEVICE = 1117;
    public const int ERROR_DEVICE_NOT_CONNECTED = 1167;
    public const int ERROR_DISK_FULL = 112;
    public const int ERROR_HANDLE_DISK_FULL = 39;
    public const int ERROR_WRITE_PROTECT = 19;

    /// <summary>
    /// Exactly the SPEC RF-05 lists: bad sector = 23, 27, 1117, 121; device/media gone = 21, 1112, 1110, 1167 and
    /// INVALID_HANDLE (6) after removal. A few codes that Windows raises for the same situation (unit/volume vanished)
    /// are classified as "gone" as well: 20, 55, 433, 1005, 1006, 1271.
    /// </summary>
    public static ReadErrorKind ClassifyRead(int code) => code switch
    {
        ERROR_CRC or ERROR_SECTOR_NOT_FOUND or ERROR_IO_DEVICE or ERROR_SEM_TIMEOUT => ReadErrorKind.BadSector,
        ERROR_NOT_READY or ERROR_NO_MEDIA_IN_DRIVE or ERROR_MEDIA_CHANGED or ERROR_DEVICE_NOT_CONNECTED or ERROR_INVALID_HANDLE => ReadErrorKind.DeviceGone,
        20 or 55 or 433 or 1005 or 1006 or 1271 => ReadErrorKind.DeviceGone,
        _ => ReadErrorKind.Other,
    };

    public static string ReadMessage(int code, ReadErrorKind kind) => kind switch
    {
        ReadErrorKind.BadSector => $"Erro de leitura no disco (trecho ilegível). Código {code}.",
        ReadErrorKind.DeviceGone => $"O leitor foi desconectado ou o disco foi removido. Código {code}.",
        _ => code == ERROR_SHARING_VIOLATION
            ? "O disco está em uso exclusivo por outro programa. Feche o outro programa que usa o disco e tente de novo. Código 32."
            : code == ERROR_ACCESS_DENIED
                ? "Acesso negado ao arquivo do disco. Código 5."
                : $"Erro ao ler o disco. Código {code}.",
    };

    public static DiscReadException ToReadException(int code, Exception? inner = null)
    {
        var kind = ClassifyRead(code);
        return new DiscReadException(kind, code, ReadMessage(code, kind), inner);
    }

    public static DiscReadException Timeout(TimeSpan timeout) =>
        new(ReadErrorKind.BadSector, ERROR_SEM_TIMEOUT,
            $"A leitura não respondeu em {timeout.TotalSeconds:0.#} s e foi interrompida.");

    /// <summary>Extracts the Win32 code from an IOException/UnauthorizedAccessException HResult; 0 when unknown.</summary>
    public static int FromException(Exception ex)
    {
        if (ex is UnauthorizedAccessException) return ERROR_ACCESS_DENIED;
        if (ex is DirectoryNotFoundException && (ex.HResult & 0xFFFF) == 0) return ERROR_PATH_NOT_FOUND;
        var hr = ex.HResult;
        if ((hr & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000)) return hr & 0xFFFF;
        if (ex is FileNotFoundException) return ERROR_FILE_NOT_FOUND;
        if (ex is DirectoryNotFoundException) return ERROR_PATH_NOT_FOUND;
        return 0;
    }
}
