namespace FFTModLoader.ContentExpansion.Runtime;

/// <summary>Observation cannot suppress, retry or change the native call.</summary>
public static class NativeObservation
{
    public static T Invoke<T>(Func<T> original, Action? before, Action<T>? after,
        Action<Exception> failure)
    {
        try { before?.Invoke(); }
        catch (Exception ex) { Report(failure, ex); }
        // Deliberately outside the observer catch: never retry native code.
        T result = original();
        try { after?.Invoke(result); }
        catch (Exception ex) { Report(failure, ex); }
        return result;
    }

    private static void Report(Action<Exception> failure, Exception ex)
    {
        try { failure(ex); } catch (Exception) { }
    }
}
