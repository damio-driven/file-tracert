namespace FileTracert.Tests.Host;

/// <summary>Polls an async condition until it holds or a timeout elapses.</summary>
internal static class TestPolling
{
    public static async Task WaitUntilAsync(Func<Task<bool>> condition, int timeoutSeconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Condition not met within {timeoutSeconds}s.");
    }
}
