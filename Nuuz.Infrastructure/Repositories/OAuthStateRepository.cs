using Google.Cloud.Firestore;
using Nuuz.Application.Abstraction;
using Nuuz.Domain.Entities;

namespace Nuuz.Infrastructure.Repositories;

public interface IOAuthStateRepository : IBaseRepository<OAuthState> { }
public sealed class OAuthStateRepository : BaseRepository<OAuthState>, IOAuthStateRepository
{
    public OAuthStateRepository(FirestoreDb db) : base(db, "OAuthStates") { }
}
