using Google.Cloud.Firestore;
using Nuuz.Application.Abstraction;
using Nuuz.Domain.Entities;

namespace Nuuz.Infrastructure.Repositories;

public interface IShareEventRepository : IBaseRepository<ShareEvent> { }
public sealed class ShareEventRepository : BaseRepository<ShareEvent>, IShareEventRepository
{
    public ShareEventRepository(FirestoreDb db) : base(db, "ShareEvents") { }
}
