// Afallon Item Tool by soybean_alien
// https://github.com/soybean-alien/afallon-item-tool
// Licensed under the GNU GPL v3 with additional terms (author attribution required,
// GPL-3.0 section 7(b)/(c)). See LICENSE and NOTICE.md.

namespace AfallonItemSpawner.Core;

public interface IItemGrantService
{
    SpawnExecutionResult Grant(ItemRecord item, int quantity);
}

