using System.Text.RegularExpressions;

namespace Maquettiste.Engine.Scripting;

/// <summary>
/// Runs a regular expression match that may be tried a second time after a match timeout. The timeout is wall-clock time, so
/// a thread that the machine does not schedule for a moment can time out on a trivial pattern; a catastrophic pattern times
/// out again on the second attempt and still fails, after twice the bound. Only a match whose first attempt left nothing
/// observable behind is retried (the caller decides, in <c>prepareRetry</c>), so the result is the one an uninterrupted match
/// gives and output stays deterministic.
/// </summary>
internal static class RegexRetry
{
    private const string RetriedKey = "maquettiste.regexRetried";

    /// <summary>
    /// Test seam: the number of attempts on this thread that time out before they run, as a stalled thread would. Zero outside tests.
    /// </summary>
    [ThreadStatic]
    internal static int SimulatedStalls;

    /// <summary>Runs <paramref name="match"/>, and once more after a match timeout when <paramref name="prepareRetry"/> allows it.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="match">The match. It may itself contain retried matches; a timeout one of them already retried is not retried again.</param>
    /// <param name="prepareRetry">
    /// Called after the first timeout, outside any exception filter: returns whether the match may run again (false when a
    /// cancellation is pending or the first attempt may have run user code), after restoring any state the first attempt changed.
    /// It may throw instead, for example the engine's own limit or cancellation.
    /// </param>
    /// <returns>The match result.</returns>
    /// <exception cref="RegexMatchTimeoutException">The match timed out and was not retried, or timed out again.</exception>
    public static T Run<T>(Func<T> match, Func<bool> prepareRetry)
    {
        try
        {
            return Attempt(match);
        }
        catch (RegexMatchTimeoutException first) when (!first.Data.Contains(RetriedKey))
        {
            if (!prepareRetry())
            {
                first.Data[RetriedKey] = true;
                throw;
            }

            try
            {
                return Attempt(match);
            }
            catch (RegexMatchTimeoutException second)
            {
                second.Data[RetriedKey] = true;
                throw;
            }
        }
    }

    private static T Attempt<T>(Func<T> match)
    {
        if (SimulatedStalls > 0)
        {
            SimulatedStalls--;
            throw new RegexMatchTimeoutException("", "(simulated stall)", TimeSpan.Zero);
        }

        return match();
    }
}
