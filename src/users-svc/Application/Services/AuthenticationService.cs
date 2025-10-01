using Application.DTO.AuthenticationDTO;
using Application.DTO.UsersDTO;
using Domain.Entities;
using Domain.Interfaces.Repositories;
using Domain.Interfaces.Services;
using Domain.Models.Response;
using Helpers.Extensions;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Bson;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace Application.Services
{
    public class AuthenticationService(IUserRepository UserRepository, IConfiguration Configuration) : IAuthenticationService
    {
        public async Task<ResponseModel<ObjectId>> Register(RegisterUserDTO registerUserRequest)
        {
            var validationResult = registerUserRequest.Validate();
            if (validationResult.HasError)
                return ResponseModel<ObjectId>.BadRequest(validationResult.ToString());

            User user = await UserRepository.FindOneAsync(user => user.Email == registerUserRequest.Email);
            if (user is not null)
                return ResponseModel<ObjectId>.BadRequest("Email de Usuário já registrado");

            var result = await UserRepository.CreateAsync(registerUserRequest);

            return ResponseModel<ObjectId>.Ok(result._id);
        }


        public async Task<ResponseModel<AuthenticationTokenDTO>> Login(LoginUserDTO loginUserRequest)
        {
            var validationResult = loginUserRequest.Validate();
            if (validationResult.HasError)
                return ResponseModel<AuthenticationTokenDTO>.BadRequest(validationResult.ToString());

            User user = await UserRepository.FindOneAsync(user => user.Email == loginUserRequest.Email);
            if (user is null)
                return ResponseModel<AuthenticationTokenDTO>.BadRequest("Login Inválido");

            if (user.Password.Equals(loginUserRequest.Password.ToHash()))
            {
                return ResponseModel<AuthenticationTokenDTO>.Ok(GenerateToken(user));
            }

            return ResponseModel<AuthenticationTokenDTO>.Unauthorized("O usuário não pode ser autenticado, verifique suas informações.");
        }

        private AuthenticationTokenDTO GenerateToken(User user)
        {
            List<Claim> claims = new()
            {
                new Claim("UserId", user._id.ToString()),
                new Claim(JwtRegisteredClaimNames.Sub, user._id.ToString()),
                new Claim(JwtRegisteredClaimNames.Name, user.Name),
                new Claim(JwtRegisteredClaimNames.Email, user.Email),
                new Claim(ClaimTypes.Role, user.Role.ToString())
            };

            var jwtSettings = Configuration.GetJwtOptions()!;

            var key = new SymmetricSecurityKey(Encoding.UTF8
                .GetBytes(jwtSettings.Key!));

            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(claims),
                Expires = DateTime.UtcNow.Add(TimeSpan.FromHours(8)),
                Issuer = jwtSettings.Issuer,
                Audience = jwtSettings.Audience,
                SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256Signature)
            };

            var tokenHandler = new JwtSecurityTokenHandler();

            var token = new JwtSecurityTokenHandler().WriteToken(tokenHandler.CreateToken(tokenDescriptor));

            return new AuthenticationTokenDTO
            {
                Token = token,
                ExpiresOn = tokenDescriptor.Expires,
                UserInfo = new ProjectUserDTO(user)
            };
        }
    }
}
