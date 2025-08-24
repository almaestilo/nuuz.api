using Nuuz.Application.DTOs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nuuz.Application.Services;
public interface IInterestService
{
    /// <summary>
    /// Returns the list of all available interests.
    /// </summary>
    Task<IEnumerable<InterestDto>> GetAllAsync();
        Task<InterestDto> CreateAsync(string name);

}
