// Nuuz.Infrastructure/Repositories/BaseRepository.cs
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Google.Cloud.Firestore;
using Nuuz.Application.Abstraction;

namespace Nuuz.Infrastructure.Repositories
{
    /// <summary>
    /// Firestore-backed implementation of IBaseRepository.
    /// Supports entities with either Guid or string Id properties.
    /// </summary>
    public class BaseRepository<T> : IBaseRepository<T> where T : class, new()
    {
        protected readonly FirestoreDb _firestoreDb;
        protected readonly string _collectionName;

        public BaseRepository(FirestoreDb firestoreDb, string collectionName)
        {
            _firestoreDb = firestoreDb;
            _collectionName = collectionName;
        }

        public async Task<List<T>> GetAllAsync()
        {
            var query = _firestoreDb.Collection(_collectionName);
            var snapshot = await query.GetSnapshotAsync();

            var results = new List<T>();
            foreach (var doc in snapshot.Documents)
            {
                if (!doc.Exists) continue;
                var entity = doc.ConvertTo<T>();
                SetEntityId(entity, doc.Id);
                results.Add(entity);
            }
            return results;
        }

        // ← If your interface now takes a string ID:
        public async Task<T?> GetAsync(string id)
        {
            var docRef = _firestoreDb.Collection(_collectionName).Document(id);
            var snap = await docRef.GetSnapshotAsync();
            if (!snap.Exists) return null;

            var entity = snap.ConvertTo<T>();
            SetEntityId(entity, snap.Id);
            return entity;
        }

        public async Task<T> AddAsync(T entity)
        {
            var docId = EnsureEntityId(entity);
            var docRef = _firestoreDb.Collection(_collectionName).Document(docId);
            await docRef.SetAsync(entity);
            return entity;
        }

        public async Task<T> UpdateAsync(T entity)
        {
            var docId = GetEntityId(entity);
            var docRef = _firestoreDb.Collection(_collectionName).Document(docId);
            await docRef.SetAsync(entity, SetOptions.MergeAll);
            return entity;
        }

        // ← If your interface now takes a string ID:
        public async Task DeleteAsync(string id)
        {
            var docRef = _firestoreDb.Collection(_collectionName).Document(id);
            await docRef.DeleteAsync();
        }

        public async Task DeleteAllAsync()
        {
            var collection = _firestoreDb.Collection(_collectionName);
            var snapshot = await collection.GetSnapshotAsync();
            var batch = _firestoreDb.StartBatch();

            foreach (var doc in snapshot.Documents)
                batch.Delete(doc.Reference);

            await batch.CommitAsync();
        }

        public async Task<List<T>> QueryRecordsAsync(Query query)
        {
            var snapshot = await query.GetSnapshotAsync();
            var results = new List<T>();

            foreach (var doc in snapshot.Documents)
            {
                if (!doc.Exists) continue;
                var entity = doc.ConvertTo<T>();
                SetEntityId(entity, doc.Id);
                results.Add(entity);
            }
            return results;
        }

        #region Private Helpers

        private void SetEntityId(T entity, string docId)
        {
            var prop = typeof(T).GetProperty("Id");
            if (prop == null) return;

            if (prop.PropertyType == typeof(Guid))
                prop.SetValue(entity, Guid.Parse(docId));
            else if (prop.PropertyType == typeof(string))
                prop.SetValue(entity, docId);
        }

        /// <summary>
        /// Ensures the entity.Id is populated (generating one if empty) and returns the string ID.
        /// </summary>
        private string EnsureEntityId(T entity)
        {
            var prop = typeof(T).GetProperty("Id")
                      ?? throw new InvalidOperationException("Entity must have an Id property.");

            if (prop.PropertyType == typeof(Guid))
            {
                var id = (Guid)prop.GetValue(entity)!;
                if (id == Guid.Empty)
                {
                    id = Guid.NewGuid();
                    prop.SetValue(entity, id);
                }
                return id.ToString();
            }
            else if (prop.PropertyType == typeof(string))
            {
                var id = (string)prop.GetValue(entity)!;
                if (string.IsNullOrWhiteSpace(id))
                {
                    id = Guid.NewGuid().ToString();
                    prop.SetValue(entity, id);
                }
                return id;
            }
            else
            {
                throw new InvalidOperationException("Entity.Id must be a Guid or string.");
            }
        }

        /// <summary>
        /// Reads entity.Id and returns it as a string.
        /// </summary>
        private string GetEntityId(T entity)
        {
            var prop = typeof(T).GetProperty("Id")
                      ?? throw new InvalidOperationException("Entity must have an Id property.");

            if (prop.PropertyType == typeof(Guid))
                return ((Guid)prop.GetValue(entity)!).ToString();
            else if (prop.PropertyType == typeof(string))
                return (string)prop.GetValue(entity)!;
            else
                throw new InvalidOperationException("Entity.Id must be a Guid or string.");
        }

        #endregion
    }
}
