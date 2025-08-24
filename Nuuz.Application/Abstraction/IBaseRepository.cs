// Nuuz.Application/RepositoryAbstraction/IBaseRepository.cs
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Google.Cloud.Firestore;

namespace Nuuz.Application.Abstraction
{
    /// <summary>
    /// Generic Firestore repository abstraction for CRUD operations.
    /// </summary>
    public interface IBaseRepository<T>
    {
        Task<List<T>> GetAllAsync();
        Task<T?> GetAsync(string id);
        Task<T> AddAsync(T entity);
        Task<T> UpdateAsync(T entity);
        Task DeleteAsync(string id);
        Task DeleteAllAsync();
        Task<List<T>> QueryRecordsAsync(Query query);
    }
}
