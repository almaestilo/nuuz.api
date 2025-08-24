using Nuuz.Application.DTOs;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Nuuz.Application.Services
{
    public interface IUserService
    {
        /// <summary>Retrieves the profile and selected interests for a given user.</summary>
        Task<UserDto> GetByIdAsync(Guid userId);

        /// <summary>Gets an existing user by Firebase UID or throws if not found.</summary>
        Task<UserDto> GetByFirebaseUidAsync(string firebaseUid);

        /// <summary>Creates a new user explicitly. Throws if already exists.</summary>
        Task<UserDto> CreateUserAsync(string firebaseUid, string displayName);

        /// <summary>
        /// Ensures a user exists for this Firebase UID. If missing, creates it using Firebase Admin
        /// (email + display name), otherwise returns the existing user. Idempotent.
        /// </summary>
        Task<UserDto> EnsureUserAsync(string firebaseUid, string? displayNameHint = null);

        /// <summary>Updates the interests selected by the user.</summary>
        Task SetUserInterestsAsync(string firebaseUid, IEnumerable<string> interestIds);

        Task<CustomInterestDto> AddCustomInterestAsync(string firebaseUid, string name);
        Task RemoveCustomInterestAsync(string firebaseUid, string customInterestId);
    }
}
