using Application.DTO.AuthenticationDTO;
using Domain.Models.Response;
using MongoDB.Bson;

namespace Domain.Interfaces.Services
{
    public interface IAuthenticationService
    {
        Task<ResponseModel<ObjectId>> Register(RegisterUserDTO registerRequest);
        Task<ResponseModel<AuthenticationTokenDTO>> Login(LoginUserDTO loginUserRequest);
    }
}
