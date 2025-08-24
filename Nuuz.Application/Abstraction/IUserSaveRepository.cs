using Nuuz.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nuuz.Application.Abstraction;
public interface IUserSaveRepository : IBaseRepository<UserSave>
{
    Task<UserSave?> GetByUserAndArticleAsync(string userId, string articleId);
    Task<List<UserSave>> GetByUserAsync(string userId, int limit, string? afterId = null);
    Task AddOrUpdateAsync(string id, UserSave entity);
}
