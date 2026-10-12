using MediaRescue.Core;

namespace MediaRescue.App.Services;

/// <summary>Stand-in used when no converter was registered (e.g. a minimal test graph): the MP4 option is simply unavailable.</summary>
public sealed class NullMp4Converter : IMp4Converter
{
    public static readonly NullMp4Converter Instance = new();

    public bool IsAvailable => false;

    public Task<Mp4ConversionResult> ConvertAsync(string discFolder, IReadOnlyList<FileResult> files, IProgress<Mp4ConversionProgress> progress, CancellationToken ct) =>
        Task.FromResult(new Mp4ConversionResult { Error = "A conversão para MP4 não está disponível nesta instalação." });
}
