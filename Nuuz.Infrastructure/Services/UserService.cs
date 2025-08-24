using FirebaseAdmin.Auth;
using Nuuz.Application.Abstraction;
using Nuuz.Application.DTOs;
using Nuuz.Application.Services;
using Nuuz.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Nuuz.Infrastructure.Services
{
    public class UserService : IUserService
    {
        private readonly IUserRepository _userRepository;
        private readonly IInterestRepository _interestRepository;

        public UserService(
            IUserRepository userRepository,
            IInterestRepository interestRepository)
        {
            _userRepository = userRepository;
            _interestRepository = interestRepository;
        }

        // -------------------------
        // CREATE / ENSURE / GET
        // -------------------------

        public async Task<UserDto> CreateUserAsync(string firebaseUid, string displayName)
        {
            // 1) Ensure not exists
            var existing = await _userRepository.GetByFirebaseUidAsync(firebaseUid);
            if (existing != null)
                throw new InvalidOperationException(
                    $"A user with Firebase UID '{firebaseUid}' already exists.");

            // 2) Fetch Firebase record (email may be null for Twitter)
            var userRecord = await FirebaseAuth.DefaultInstance.GetUserAsync(firebaseUid);

            var email = userRecord.Email ?? string.Empty; // ← tolerate missing email
            var resolvedDisplay = ResolveDisplayName(
                explicitHint: displayName,
                userRecordDisplayName: userRecord.DisplayName,
                email: userRecord.Email,
                firebaseUid: firebaseUid);

            // 3) Create domain user
            var user = new User
            {
                Id = Guid.NewGuid().ToString(),
                FirebaseUid = firebaseUid,
                Email = email,                 // empty string if none
                DisplayName = resolvedDisplay,
                InterestIds = new List<string>(),
                CustomInterests = new List<CustomInterest>()
            };

            // 4) Persist
            var created = await _userRepository.AddAsync(user);

            // 5) Return DTO
            return await MapToDtoAsync(created);
        }

        /// <summary>
        /// Idempotent: if user exists, returns it; if not, creates from Firebase Admin user record.
        /// Backwards-compatible with providers that don't return an email (e.g., Twitter).
        /// </summary>
        public async Task<UserDto> EnsureUserAsync(string firebaseUid, string? displayNameHint = null)
        {
            var existing = await _userRepository.GetByFirebaseUidAsync(firebaseUid);
            if (existing != null)
            {
                // Normalize null collections (older records)
                existing.InterestIds ??= new List<string>();
                existing.CustomInterests ??= new List<CustomInterest>();
                return await MapToDtoAsync(existing);
            }

            // Not found → hydrate from Firebase (email may be null)
            var userRecord = await FirebaseAuth.DefaultInstance.GetUserAsync(firebaseUid);

            var email = userRecord.Email ?? string.Empty; // ← tolerate missing email
            var resolvedDisplay = ResolveDisplayName(
                explicitHint: displayNameHint,
                userRecordDisplayName: userRecord.DisplayName,
                email: userRecord.Email,
                firebaseUid: firebaseUid);

            var user = new User
            {
                Id = Guid.NewGuid().ToString(),
                FirebaseUid = firebaseUid,
                Email = email,                 // empty string if none
                DisplayName = resolvedDisplay,
                InterestIds = new List<string>(),
                CustomInterests = new List<CustomInterest>()
            };

            var created = await _userRepository.AddAsync(user);
            return await MapToDtoAsync(created);
        }

        public async Task<UserDto> GetByFirebaseUidAsync(string firebaseUid)
        {
            var user = await _userRepository.GetByFirebaseUidAsync(firebaseUid)
                ?? throw new KeyNotFoundException(
                    $"User with Firebase UID '{firebaseUid}' not found.");

            user.InterestIds ??= new List<string>();
            user.CustomInterests ??= new List<CustomInterest>();

            return await MapToDtoAsync(user);
        }

        public async Task<UserDto> GetByIdAsync(Guid userId)
        {
            var user = await _userRepository.GetAsync(userId.ToString())
                ?? throw new KeyNotFoundException($"User '{userId}' not found.");

            user.InterestIds ??= new List<string>();
            user.CustomInterests ??= new List<CustomInterest>();

            return await MapToDtoAsync(user);
        }

        // -------------------------
        // INTERESTS
        // -------------------------

        public async Task SetUserInterestsAsync(string firebaseUid, IEnumerable<string> interestIds)
        {
            var user = await _userRepository.GetByFirebaseUidAsync(firebaseUid)
                ?? throw new KeyNotFoundException($"User with Firebase UID '{firebaseUid}' not found.");

            user.InterestIds ??= new List<string>();
            user.CustomInterests ??= new List<CustomInterest>();

            // Normalize + dedupe
            var ids = (interestIds ?? Enumerable.Empty<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            user.InterestIds = ids;
            await _userRepository.UpdateAsync(user);
        }

        public async Task<CustomInterestDto> AddCustomInterestAsync(string firebaseUid, string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Name is required.", nameof(name));

            var user = await _userRepository.GetByFirebaseUidAsync(firebaseUid)
                ?? throw new KeyNotFoundException($"User with Firebase UID '{firebaseUid}' not found.");

            user.InterestIds ??= new List<string>();
            user.CustomInterests ??= new List<CustomInterest>();

            var trimmed = name.Trim();

            // prevent dupes by name (case-insensitive)
            var existing = user.CustomInterests
                .FirstOrDefault(ci => string.Equals(ci.Name, trimmed, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
                return new CustomInterestDto { Id = existing.Id, Name = existing.Name };

            var created = new CustomInterest
            {
                Id = Guid.NewGuid().ToString(),
                Name = trimmed
            };

            user.CustomInterests.Add(created);
            await _userRepository.UpdateAsync(user);

            return new CustomInterestDto { Id = created.Id, Name = created.Name };
        }

        public async Task RemoveCustomInterestAsync(string firebaseUid, string customInterestId)
        {
            var user = await _userRepository.GetByFirebaseUidAsync(firebaseUid)
                ?? throw new KeyNotFoundException($"User with Firebase UID '{firebaseUid}' not found.");

            user.InterestIds ??= new List<string>();
            user.CustomInterests ??= new List<CustomInterest>();

            var removed = user.CustomInterests.RemoveAll(ci =>
                string.Equals(ci.Id, customInterestId, StringComparison.OrdinalIgnoreCase));

            if (removed > 0)
                await _userRepository.UpdateAsync(user);
        }

        // -------------------------
        // MAPPING HELPERS
        // -------------------------

        private async Task<UserDto> MapToDtoAsync(User user)
        {
            // Resolve interest names from IDs
            var interestDtos = new List<InterestDto>();
            var ids = user.InterestIds ?? new List<string>();

            foreach (var interestId in ids)
            {
                if (string.IsNullOrWhiteSpace(interestId)) continue;

                var domainInterest = await _interestRepository.GetAsync(interestId);
                if (domainInterest != null)
                {
                    interestDtos.Add(new InterestDto
                    {
                        Id = domainInterest.Id,
                        Name = domainInterest.Name
                    });
                }
            }

            var customDtos = (user.CustomInterests ?? new List<CustomInterest>())
                .Select(ci => new CustomInterestDto { Id = ci.Id, Name = ci.Name });

            return new UserDto
            {
                Id = user.Id,
                Email = user.Email,                 // may be empty string
                DisplayName = user.DisplayName,
                Interests = interestDtos,
                CustomInterests = customDtos
            };
        }

        // -------------------------
        // INTERNAL HELPERS
        // -------------------------

        /// <summary>
        /// Chooses a reasonable display name in priority order:
        /// explicit hint → Firebase displayName → email local-part → uid fallback.
        /// </summary>
        private static string ResolveDisplayName(string? explicitHint, string? userRecordDisplayName, string? email, string firebaseUid)
        {
            if (!string.IsNullOrWhiteSpace(explicitHint))
                return explicitHint!.Trim();

            if (!string.IsNullOrWhiteSpace(userRecordDisplayName))
                return userRecordDisplayName!;

            var local = email?.Split('@').FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(local))
                return local!;

            var take = Math.Min(firebaseUid.Length, 6);
            return $"user_{firebaseUid[..take]}";
        }
    }
}
