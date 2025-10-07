using Application.DTO.Bases;
using Application.DTO.Bases.Interfaces;
using Application.DTO.UsersDTO;
using Domain.Entities;
using Domain.Interfaces.Repositories;
using Domain.Interfaces.Services;
using Domain.Models.Response;
using MongoDB.Bson;

namespace Application.Services
{
    public class UserService(IUserRepository UserRepository, IEventRepository EventRepository) : IUserService
    {
        public async Task<List<ProjectUserDTO>> GetAllAsync()
        {
            var items = await UserRepository.GetAllAsync<ProjectUserDTO>();

            var ev = DomainEvent.Create(
                aggregateId: ObjectId.Empty,
                type: "UsersListed",
                data: new Dictionary<string, object?>
                {
                    ["Count"] = items?.Count ?? 0
                }
            );
            await EventRepository.AppendEventAsync(ev, CancellationToken.None);

            return items;
        }

        public async Task<ProjectUserDTO?> GetByIdAsync(ObjectId id)
        {
            var dto = await UserRepository.GetByIdAsync<ProjectUserDTO>(id);

            var ev = DomainEvent.Create(
                aggregateId: id,
                type: dto is null ? "UserNotFound" : "UserFetched",
                data: new Dictionary<string, object?>
                {
                    ["UserId"] = id.ToString(),
                    ["Found"] = dto is not null
                }
            );
            await EventRepository.AppendEventAsync(ev, CancellationToken.None);

            return dto;
        }

        public async Task<List<ProjectUserDTO>> FindUsersAsync(FilterUserDTO filterDto)
        {
            var list = await UserRepository.FindAsync<ProjectUserDTO>(filterDto);

            var ev = DomainEvent.Create(
                aggregateId: ObjectId.Empty,
                type: "UserFilterQueried",
                data: new Dictionary<string, object?>
                {
                    ["Filter"] = filterDto,
                    ["Count"] = list?.Count ?? 0
                }
            );
            await EventRepository.AppendEventAsync(ev, CancellationToken.None);

            return list;
        }

        public async Task<ResponseModel<ProjectUserDTO>> CreateAsync(CreateUserDTO createDto)
        {
            return await ProcessCriate(createDto);
        }

        public async Task<ResponseModel<ProjectUserDTO>> CreateAdminAsync(CreateUserAdminDTO createDto)
        {
            return await ProcessCriate(createDto);
        }

        public async Task UpdateAsync(ObjectId id, UpdateUserDTO updateDto)
        {
            await UserRepository.UpdateAsync(id, updateDto);

            var ev = DomainEvent.Create(
                aggregateId: id,
                type: "UserUpdated",
                data: new Dictionary<string, object?>
                {
                    ["UserId"] = id.ToString(),
                    ["Changes"] = updateDto
                }
            );
            await EventRepository.AppendEventAsync(ev, CancellationToken.None);
        }

        public async Task DeleteAsync(ObjectId id)
        {
            await UserRepository.DeleteAsync(id);

            var ev = DomainEvent.Create(
                aggregateId: id,
                type: "UserDeleted",
                data: new Dictionary<string, object?>
                {
                    ["UserId"] = id.ToString()
                }
            );
            await EventRepository.AppendEventAsync(ev, CancellationToken.None);
        }

        public async Task<User> GetAdmin()
        {
            var admin = await UserRepository.FindOneAsync(x => x.Role == Domain.Enums.UserRole.Admin);

            var ev = DomainEvent.Create(
                aggregateId: admin?._id ?? ObjectId.Empty,
                type: admin is null ? "AdminNotFound" : "AdminFetched",
                data: new Dictionary<string, object?>
                {
                    ["Found"] = admin is not null,
                    ["AdminId"] = admin?._id.ToString()
                }
            );
            await EventRepository.AppendEventAsync(ev, CancellationToken.None);

            return admin;
        }

        private async Task<ResponseModel<ProjectUserDTO>> ProcessCriate<T>(T createDto)
            where T : BaseCreateDTO<User>, IValidator
        {
            var validationResult = createDto.Validate();

            if (validationResult.HasError)
            {
                var evFail = DomainEvent.Create(
                    aggregateId: ObjectId.Empty,
                    type: "UserCreateValidationFailed",
                    data: new Dictionary<string, object?>
                    {
                        ["Errors"] = validationResult.ToString(),
                        ["InputType"] = typeof(T).Name
                    }
                );
                await EventRepository.AppendEventAsync(evFail, CancellationToken.None);

                return ResponseModel<ProjectUserDTO>.BadRequest(validationResult.ToString());
            }

            var entity = await UserRepository.CreateAsync(createDto);
            var resultModel = await UserRepository.GetByIdAsync<ProjectUserDTO>(entity._id);

            var ev = DomainEvent.Create(
                aggregateId: entity._id,
                type: "UserCreated",
                data: new Dictionary<string, object?>
                {
                    ["UserId"] = entity._id.ToString(),
                    ["Name"] = resultModel?.Name,
                    ["Email"] = resultModel?.Email
                }
            );
            await EventRepository.AppendEventAsync(ev, CancellationToken.None);

            return ResponseModel<ProjectUserDTO>.Created(resultModel);
        }
    }
}
