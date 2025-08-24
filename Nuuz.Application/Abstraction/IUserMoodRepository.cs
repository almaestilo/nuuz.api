using System.Threading.Tasks;
using Nuuz.Domain.Entities;

namespace Nuuz.Application.Abstraction
{
    // Inherit your common CRUD, plus a convenience Upsert
    public interface IUserMoodRepository : IBaseRepository<UserMood>
    {
        Task<UserMood> UpsertAsync(UserMood mood);
    }
}
