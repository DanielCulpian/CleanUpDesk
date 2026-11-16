namespace CleanUpDesk.Core;
public sealed class SearchTimeEstimator
{
    SearchPreparationProgress? sample;
    /// <summary>Updates the estimator from real processed/pending work and prior completed traversal statistics.</summary>
    public void Observe(SearchPreparationProgress progress) { if (sample == null || progress.CompletedDirectories >= sample.CompletedDirectories) sample = progress; }
    /// <summary>Formats a conservative remaining-time estimate or explains why a reliable estimate is not yet available.</summary>
    public string RemainingText(TimeSpan elapsed)
    {
        if (sample == null || sample.CompletedDirectories < 3 || elapsed.TotalSeconds < 1) return "Restante estimado: calculando…";
        long pending = Math.Max(sample.KnownPendingDirectories, sample.HistoricalPendingDirectories ?? 0);
        if (pending <= 0) return "Restante estimado: finalizando…";
        if (!sample.HistoricalPendingDirectories.HasValue)
            return $"Tiempo restante aún desconocido · {sample.CompletedDirectories:N0} carpetas revisadas · descubriendo el alcance";
        double seconds = elapsed.TotalSeconds / sample.CompletedDirectories * pending;
        string duration = seconds < 60 ? $"{Math.Max(1, (int)Math.Ceiling(seconds))} s" : seconds < 3600 ? $"{Math.Ceiling(seconds / 60):0} min" : $"{seconds / 3600:0.#} h";
        return $"Restante estimado: ≈ {duration} (según revisión anterior; puede variar)";
    }
}
