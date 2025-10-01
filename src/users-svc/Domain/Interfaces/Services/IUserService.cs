using Application.DTO.UsersDTO;
using Domain.Models.Response;
using Domain.Entities;
using MongoDB.Bson;

namespace Domain.Interfaces.Services
{
    public interface IUserService
    {
        Task<ResponseModel<ProjectUserDTO>> CreateAdminAsync(CreateUserAdminDTO createDto);
        Task<ResponseModel<ProjectUserDTO>> CreateAsync(CreateUserDTO createDto);
        Task DeleteAsync(ObjectId id);
        Task<List<ProjectUserDTO>> FindUsersAsync(FilterUserDTO filterDto);
        Task<User> GetAdmin();
        Task<List<ProjectUserDTO>> GetAllAsync();
        Task<ProjectUserDTO?> GetByIdAsync(ObjectId id);
        Task UpdateAsync(ObjectId id, UpdateUserDTO updateDto);
    }
}
