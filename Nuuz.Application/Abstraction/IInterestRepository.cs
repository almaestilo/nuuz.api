using Nuuz.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nuuz.Application.Abstraction;
/// <summary>
/// Repository for <see cref="Interest"/> entities.
/// </summary>
public interface IInterestRepository : IBaseRepository<Interest>
{
    Task<List<Interest>> GetAllOrderedAsync();
    Task<Interest?> GetByNameAsync(string name);
}
