// Nuuz.Application.Abstraction/IFeedbackRepository.cs
using Nuuz.Domain.Entities;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Nuuz.Application.Abstraction
{
    public interface IFeedbackRepository
    {
        Task<Feedback> AddAsync(Feedback feedback);
        Task<Feedback?> GetAsync(string id);
        Task<IReadOnlyList<Feedback>> ListByUserAsync(string firebaseUid, int take = 50);
    }
}
