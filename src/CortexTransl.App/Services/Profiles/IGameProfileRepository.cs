using CortexTransl.App.Models;

namespace CortexTransl.App.Services.Profiles;

public interface IGameProfileRepository
{
    Task<IReadOnlyList<GameProfile>> GetAllAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(GameProfile profile, CancellationToken cancellationToken = default);
}
