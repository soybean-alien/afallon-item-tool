namespace AfallonItemSpawner.Core;

public sealed record SpawnExecutionResult(bool Succeeded, string Message)
{
    public static SpawnExecutionResult Success(string message) => new(true, message);
    public static SpawnExecutionResult Failure(string message) => new(false, message);
}

