using AfallonItemSpawner.Core;

var tests = new (string Name, Action Run)[]
{
    ("catalog rejects duplicate IDs", DuplicateIdsAreRejected),
    ("search ignores case and supports multiple tokens", SearchSupportsTokens),
    ("search filters categories", SearchFiltersCategories),
    ("search handles blank names safely", BlankNamesAreSafe),
    ("policy rejects invalid quantities", InvalidQuantitiesAreRejected),
    ("policy rejects absent IDs", AbsentIdsAreRejected),
    ("policy blocks quest and progression items by default", SensitiveItemsAreBlocked),
    ("policy allows reviewed sensitive items only when explicitly enabled", SensitiveOverrideIsExplicit),
    ("policy allows an ordinary catalog item", OrdinaryItemIsAllowed),
    ("coordinator never calls adapter for denied requests", DeniedRequestDoesNotCallAdapter),
    ("coordinator calls adapter once for valid requests", ValidRequestCallsAdapter),
    ("coordinator contains adapter exceptions", AdapterExceptionsAreContained)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"FAIL {test.Name}: {exception.Message}");
    }
}

foreach (var failure in failures)
{
    Console.Error.WriteLine(failure);
}

Console.WriteLine($"RESULT {tests.Length - failures.Count}/{tests.Length} passed");
return failures.Count == 0 ? 0 : 1;

static ItemCatalog SampleCatalog() => new(new[]
{
    new ItemRecord(0, "Iron Ore", ItemCategory.CraftingSupply, "Metal"),
    new ItemRecord(7, "Traveler Ring", ItemCategory.Equipment, "Ring"),
    new ItemRecord(8, "Ancient Key", ItemCategory.Quest),
    new ItemRecord(9, "Royal Token", ItemCategory.Miscellaneous, IsProgressionSensitive: true),
    new ItemRecord(10, "", ItemCategory.Unknown)
});

static void DuplicateIdsAreRejected()
{
    AssertThrows<ArgumentException>(() => new ItemCatalog(new[]
    {
        new ItemRecord(1, "A", ItemCategory.Miscellaneous),
        new ItemRecord(1, "B", ItemCategory.Miscellaneous)
    }));
}

static void SearchSupportsTokens()
{
    var result = SampleCatalog().Search(new CatalogQuery("trav RING"));
    Assert(result.Count == 1 && result[0].Id == 7, "Expected Traveler Ring only.");
}

static void SearchFiltersCategories()
{
    var result = SampleCatalog().Search(new CatalogQuery(Category: ItemCategory.CraftingSupply));
    Assert(result.Count == 1 && result[0].Id == 0, "Expected crafting item ID 0.");
}

static void BlankNamesAreSafe()
{
    var result = SampleCatalog().Search(new CatalogQuery("Item 10"));
    Assert(result.Count == 1 && result[0].SafeName == "Item 10", "Expected safe fallback name.");
}

static void InvalidQuantitiesAreRejected()
{
    var policy = new SpawnPolicy(maximumQuantity: 25);
    Assert(!policy.Validate(SampleCatalog(), new SpawnRequest(0, 0)).IsAllowed, "Zero was allowed.");
    Assert(!policy.Validate(SampleCatalog(), new SpawnRequest(0, 26)).IsAllowed, "Over-limit was allowed.");
}

static void AbsentIdsAreRejected()
{
    var result = new SpawnPolicy().Validate(SampleCatalog(), new SpawnRequest(404, 1));
    Assert(!result.IsAllowed, "Unknown ID was allowed.");
}

static void SensitiveItemsAreBlocked()
{
    var policy = new SpawnPolicy();
    Assert(!policy.Validate(SampleCatalog(), new SpawnRequest(8, 1)).IsAllowed, "Quest item was allowed.");
    Assert(!policy.Validate(SampleCatalog(), new SpawnRequest(9, 1)).IsAllowed, "Progression item was allowed.");
}

static void SensitiveOverrideIsExplicit()
{
    var policy = new SpawnPolicy(allowProgressionSensitiveItems: true);
    Assert(policy.Validate(SampleCatalog(), new SpawnRequest(8, 1)).IsAllowed, "Explicit override failed.");
}

static void OrdinaryItemIsAllowed()
{
    var result = new SpawnPolicy().Validate(SampleCatalog(), new SpawnRequest(0, 3));
    Assert(result.IsAllowed && result.Item?.Id == 0, "Ordinary item was denied.");
}

static void DeniedRequestDoesNotCallAdapter()
{
    var adapter = new RecordingGrantService();
    var coordinator = new SpawnCoordinator(SampleCatalog(), new SpawnPolicy(), adapter);
    var result = coordinator.Execute(new SpawnRequest(8, 1));
    Assert(!result.Succeeded && adapter.CallCount == 0, "Denied request reached adapter.");
}

static void ValidRequestCallsAdapter()
{
    var adapter = new RecordingGrantService();
    var coordinator = new SpawnCoordinator(SampleCatalog(), new SpawnPolicy(), adapter);
    var result = coordinator.Execute(new SpawnRequest(0, 2));
    Assert(result.Succeeded && adapter.CallCount == 1, "Valid request did not reach adapter exactly once.");
    Assert(adapter.LastItem?.Id == 0 && adapter.LastQuantity == 2, "Adapter received wrong request.");
}

static void AdapterExceptionsAreContained()
{
    var coordinator = new SpawnCoordinator(SampleCatalog(), new SpawnPolicy(), new ThrowingGrantService());
    var result = coordinator.Execute(new SpawnRequest(0, 1));
    Assert(!result.Succeeded && result.Message.Contains("InvalidOperationException"),
        "Adapter exception was not converted to a safe failure.");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertThrows<T>(Action action) where T : Exception
{
    try
    {
        action();
    }
    catch (T)
    {
        return;
    }

    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

sealed class RecordingGrantService : IItemGrantService
{
    public int CallCount { get; private set; }
    public ItemRecord? LastItem { get; private set; }
    public int LastQuantity { get; private set; }

    public SpawnExecutionResult Grant(ItemRecord item, int quantity)
    {
        CallCount++;
        LastItem = item;
        LastQuantity = quantity;
        return SpawnExecutionResult.Success("Recorded.");
    }
}

sealed class ThrowingGrantService : IItemGrantService
{
    public SpawnExecutionResult Grant(ItemRecord item, int quantity) =>
        throw new InvalidOperationException("Simulated adapter failure.");
}

