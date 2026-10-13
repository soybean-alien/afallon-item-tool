namespace AfallonItemSpawner.Core;

public interface IItemGrantService
{
    SpawnExecutionResult Grant(ItemRecord item, int quantity);
}

