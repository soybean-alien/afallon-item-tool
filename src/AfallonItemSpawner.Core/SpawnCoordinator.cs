// Afallon Item Tool by soybean_alien
// https://github.com/soybean-alien/afallon-item-tool
// Licensed under the GNU GPL v3 with additional terms (author attribution required,
// GPL-3.0 section 7(b)/(c)). See LICENSE and NOTICE.md.

namespace AfallonItemSpawner.Core;

public sealed class SpawnCoordinator
{
    private readonly ItemCatalog _catalog;
    private readonly SpawnPolicy _policy;
    private readonly IItemGrantService _grantService;

    public SpawnCoordinator(ItemCatalog catalog, SpawnPolicy policy, IItemGrantService grantService)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _grantService = grantService ?? throw new ArgumentNullException(nameof(grantService));
    }

    public SpawnExecutionResult Execute(SpawnRequest request)
    {
        var validation = _policy.Validate(_catalog, request);
        if (!validation.IsAllowed || validation.Item is null)
        {
            return SpawnExecutionResult.Failure(validation.Message);
        }

        try
        {
            return _grantService.Grant(validation.Item, request.Quantity);
        }
        catch (Exception exception)
        {
            return SpawnExecutionResult.Failure(
                $"The game adapter rejected the request: {exception.GetType().Name}.");
        }
    }
}

