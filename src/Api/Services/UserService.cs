using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using BCrypt.Net;
using Data;
using Microsoft.IdentityModel.Tokens;
using Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Services
{
    public class UserService
    {
        private readonly IMongoCollection<User> _users;
        private readonly SlugService _slugService;
        private readonly ArtistService _artistService;
        private readonly IConfiguration _configuration;

        public UserService(
            DataContext dataContext,
            SlugService slugService,
            ArtistService artistService,
            IConfiguration configuration
        )
        {
            _users = dataContext.Users;
            _slugService = slugService;
            _artistService = artistService;
            _configuration = configuration;
        }

        // interates through _users collection to find a the first user with that username
        public async Task<User?> GetUserByUsernameAsync(string username)
        {
            return await _users.Find(u => u.Username == username).FirstOrDefaultAsync();
        }

        // iterates throguh _users collection to find the first user with that email
        public async Task<User?> GetUserByEmailAsync(string email)
        {
            return await _users.Find(u => u.Email == email).FirstOrDefaultAsync();
        }

        public async Task<User> CreateUserAsync(
            string username,
            string email,
            string password,
            string role,
            string artistName
        )
        {
            // only one admin account right now..
            if (role == "listener" || role == "artist")
            {
                if (await GetUserByUsernameAsync(username) == null)
                {
                    var slug = await _slugService.GenerateSlug(username);

                    var user = new User
                    {
                        Id = ObjectId.GenerateNewId(),
                        Username = username,
                        Email = email,
                        PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow, // ADD THIS
                        IsActive = true,
                        Avatar = String.Empty,
                        Role = role,
                        Slug = slug.SlugValue,
                    };
                    if (role == "artist")
                    {
                        // their role is artist, we must create an artist
                        await _artistService.CreateAsync(
                            new Artist
                            {
                                Name = artistName,
                                Slug = slug.SlugValue,
                                Id = user.Id,
                            }
                        );
                    }
                    await _users.InsertOneAsync(user);
                    return user;
                }
                else
                {
                    throw new ArgumentException("Username already exists.");
                }
            }
            else
            {
                throw new ArgumentException(
                    "Invalid role specified. Allowed roles are 'listener' or 'artist'."
                );
            }
        }

        // Verifies the password against the stored hash
        public bool VerifyPassword(string password, string hash)
        {
            return BCrypt.Net.BCrypt.Verify(password, hash);
        }

        // gets the user from GetUserByUsernameAsync method
        // checks if user is active, not null, and verifies the password is correct
        public async Task<User?> AuthenticateAsync(string username, string password)
        {
            var user = await GetUserByUsernameAsync(username);
            if (user == null || !user.IsActive || !VerifyPassword(password, user.PasswordHash))
            {
                // returns null if user is not found, is inactive, or password does not match
                return null;
            }
            return user;
        }

        // test this
        public async Task<bool> UpdateUserAvatarAsync(string userId, string avatarUrl)
        {
            var myUserId = ObjectId.TryParse(userId, out var parsedId) ? parsedId : ObjectId.Empty;
            if (myUserId == ObjectId.Empty)
            {
                throw new ArgumentException("Invalid user ID format.");
            }
            else
            {
                var filter = Builders<User>.Filter.Eq(u => u.Id, myUserId);
                var update = Builders<User>.Update.Set(u => u.Avatar, avatarUrl);
                var result = await _users.UpdateOneAsync(filter, update);
                return result.ModifiedCount > 0;
            }
        }

        public async Task<bool> UpdateUserInfoAsync(User user)
        {
            if (user == null || user.Id == ObjectId.Empty)
            {
                throw new ArgumentException("User cannot be null and must have a valid Id.");
            }
            else
            {
                var filter = Builders<User>.Filter.Eq(u => u.Id, user.Id);
                var update = Builders<User>
                    .Update.Set(u => u.FirstName, user.FirstName)
                    .Set(u => u.LastName, user.LastName)
                    .Set(u => u.Bio, user.Bio);

                var result = await _users.UpdateOneAsync(filter, update);
                return result.ModifiedCount == 1;
            }
        }

        public async Task<User?> GetUserByIdAsync(string userId)
        {
            var myUserId = ObjectId.TryParse(userId, out var parsedId) ? parsedId : ObjectId.Empty;
            if (myUserId == ObjectId.Empty)
            {
                throw new ArgumentException("Invalid user ID format.");
            }

            return await _users.Find(u => u.Id == myUserId).FirstOrDefaultAsync();
        }

        public async Task<bool> UpdateUserProfileAsync(
            string userId,
            Controllers.UpdateProfileRequest request
        )
        {
            var filter = Builders<User>.Filter.Eq(u => u.Id, ObjectId.Parse(userId));
            var updateBuilder = Builders<User>.Update;
            var updates = new List<UpdateDefinition<User>>();

            if (!string.IsNullOrEmpty(request.FirstName))
                updates.Add(updateBuilder.Set(u => u.FirstName, request.FirstName));

            if (!string.IsNullOrEmpty(request.LastName))
                updates.Add(updateBuilder.Set(u => u.LastName, request.LastName));

            if (!string.IsNullOrEmpty(request.Bio))
                updates.Add(updateBuilder.Set(u => u.Bio, request.Bio));

            if (!string.IsNullOrEmpty(request.Avatar))
                updates.Add(updateBuilder.Set(u => u.Avatar, request.Avatar));

            if (updates.Count == 0)
                return false;

            var combinedUpdate = updateBuilder.Combine(updates);
            var result = await _users.UpdateOneAsync(filter, combinedUpdate);
            return result.ModifiedCount > 0;
        }

        // Set a specific user as admin (call this once to create your admin)
        public async Task<bool> SetUserAsAdmin(string username)
        {
            var user = await GetUserByUsernameAsync(username);
            if (user == null)
                return false;

            var filter = Builders<User>.Filter.Eq(u => u.Id, user.Id);
            var update = Builders<User>.Update.Set(u => u.Role, "admin");
            var result = await _users.UpdateOneAsync(filter, update);
            return result.ModifiedCount > 0;
        }

        public async Task<bool> UpdateUserRoleAsync(string userId, string role)
        {
            var myUserId = ObjectId.TryParse(userId, out var parsedId) ? parsedId : ObjectId.Empty;
            if (myUserId == ObjectId.Empty)
            {
                throw new ArgumentException("Invalid user ID format.");
            }
            if (string.IsNullOrEmpty(role))
            {
                throw new ArgumentException("Role cannot be null or empty.");
            }
            if (role != "admin" && role != "artist" && role != "listener")
            {
                throw new ArgumentException(
                    "Invalid role specified. Allowed roles are 'admin', 'artist', or 'listener'."
                );
            }
            // Ensure the user exists
            var user = await GetUserByIdAsync(userId);
            if (user == null)
            {
                throw new ArgumentException("User not found.");
            }
            // Update the user's role
            var filter = Builders<User>.Filter.Eq(u => u.Id, myUserId);
            var update = Builders<User>.Update.Set(u => u.Role, role);
            var result = await _users.UpdateOneAsync(filter, update);
            return result.ModifiedCount > 0;
        }

        public async Task<string> GetUserSlugAsync(string userId)
        {
            var user = await GetUserByIdAsync(userId);
            if (user == null)
                throw new ArgumentException("User not found.");

            return user.Slug;
        }

        public string GenerateToken(User user)
        {
            var jwtSettings = _configuration.GetSection("JwtSettings");
            var secretKey =
                jwtSettings["SecretKey"]
                ?? throw new InvalidOperationException("JWT SecretKey not configured");
            var issuer =
                jwtSettings["Issuer"]
                ?? throw new InvalidOperationException("JWT Issuer not configured");
            var audience =
                jwtSettings["Audience"]
                ?? throw new InvalidOperationException("JWT Audience not configured");
            var expiryInMinutes = int.Parse(jwtSettings["ExpiryInMinutes"] ?? "60");

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey));
            var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            if (user.Email == null || user.Username == null || user.Id == null)
            {
                throw new ArgumentException("User must have a valid Email, Username, and Id.");
            }
            else
            {
                var claims = new[]
                {
                    new Claim(
                        ClaimTypes.NameIdentifier,
                        user.Id.ToString() ?? ObjectId.Empty.ToString()
                    ),
                    new Claim(ClaimTypes.Name, user.Username),
                    new Claim(ClaimTypes.Email, user.Email),
                    new Claim("userId", user.Id.ToString() ?? ObjectId.Empty.ToString()),
                    new Claim(ClaimTypes.Role, user.Role ?? "user"),
                };

                var token = new JwtSecurityToken(
                    issuer: issuer,
                    audience: audience,
                    claims: claims,
                    expires: DateTime.UtcNow.AddMinutes(expiryInMinutes),
                    signingCredentials: credentials
                );

                // FIX: Add this return statement
                return new JwtSecurityTokenHandler().WriteToken(token);
            }
        }
    }
}
