namespace AfallonItemSpawner.Core;

public sealed record SpawnValidationResult(bool IsAllowed, string Message, ItemRecord? Item = null)
{
    public static SpawnValidationResult Allow(ItemRecord item) =>
        new(true, $"Ready to add {item.SafeName}.", item);

    public static SpawnValidationResult Deny(string message) => new(false, message);
}

