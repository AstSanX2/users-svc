using Application.DTO.UsersDTO;
using Application.Services;
using AutoFixture;
using Domain.Entities;
using Domain.Interfaces.Repositories;
using Domain.Interfaces.Services;
using MongoDB.Bson;
using Moq;

namespace users_svc.Tests.ServiceTests
{
    public class UserServiceTests : BaseTests
    {
        private List<User> _stubList;
        private Mock<IUserRepository> _mockRepo;
        private IUserService _service;

        public UserServiceTests()
        {
        }

        protected override void InitStubs()
        {
            _stubList = [.. _fixture.Build<User>()
                                .With(e => e._id, ObjectId.GenerateNewId())
                                .CreateMany(2)];
        }

        protected override void MockDependencies()
        {
            _mockRepo = new Mock<IUserRepository>(MockBehavior.Strict);

            _mockRepo.Setup(r => r.GetAllAsync<ProjectUserDTO>())
                .ReturnsAsync([.. _stubList!.Select(x => new ProjectUserDTO(x))]);

            _mockRepo.Setup(r => r.GetByIdAsync<ProjectUserDTO>(It.IsAny<ObjectId>()))
                .ReturnsAsync((ObjectId id) =>
                {
                    var user = _stubList?.FirstOrDefault(x => x._id == id);
                    if (user == null)
                        return null;

                    return new ProjectUserDTO(user);
                });

            _mockRepo.Setup(r => r.CreateAsync(It.IsAny<CreateUserDTO>()))
                .ReturnsAsync((CreateUserDTO dto) =>
                {
                    var entity = dto.ToEntity();
                    entity._id = ObjectId.GenerateNewId();
                    _stubList!.Add(entity);
                    return entity;
                });

            _mockRepo.Setup(r => r.UpdateAsync(It.IsAny<ObjectId>(), It.IsAny<UpdateUserDTO>()))
                .Returns(Task.CompletedTask);

            _mockRepo.Setup(r => r.DeleteAsync(It.IsAny<ObjectId>()))
                .Callback<ObjectId>(id =>
                {
                    var index = _stubList!.FindIndex(x => x._id == id);
                    if (index >= 0) _stubList!.RemoveAt(index);
                })
                .Returns(Task.CompletedTask);

            _service = new UserService(_mockRepo.Object);
        }

        [Fact(DisplayName = "GetAllAsync deve retornar todas as entidades")]
        public async Task GetAllAsync_ReturnsEntities()
        {
            var result = await _service!.GetAllAsync();

            Assert.NotNull(result);
            Assert.Equal(_stubList!.Count, result.Count);
        }

        [Fact(DisplayName = "GetByIdAsync deve retornar a entidade correspondente")]
        public async Task GetByIdAsync_ReturnsEntity()
        {
            var item = _fixture.Build<User>()
                               .With(e => e._id, ObjectId.GenerateNewId)
                               .Create();
            _stubList.Add(item);

            var result = await _service!.GetByIdAsync(item._id);

            Assert.NotNull(result);
            Assert.Equal(item._id, result!._id);
        }

        [Fact(DisplayName = "CreateAsync deve chamar o repositório e retornar o resultado esperado")]
        public async Task CreateAsync_CallsRepository_AndReturnsExpectedResult()
        {
            // Arrange
            var dto = new CreateUserDTO
            {
                Name = "Usuário Teste",
                Email = "teste@email.com",
                Password = "Senha@123"
            };

            // Act
            var response = await _service.CreateAsync(dto);

            // Assert
            Assert.False(response.HasError);
            Assert.Equal(201, response.StatusCode);
            Assert.NotNull(response.Data);

            _mockRepo.Verify(r => r.CreateAsync(dto), Times.Once);
            _mockRepo.Verify(r => r.GetByIdAsync<ProjectUserDTO>(response.Data._id), Times.Once);
        }

        [Fact(DisplayName = "CreateAsync com DTO inválido deve retornar BadRequest")]
        public async Task CreateAsync_InvalidDto_ReturnsBadRequest()
        {
            // Arrange
            var invalidDtoMock = new Mock<CreateUserDTO>();

            var invalidDto = invalidDtoMock.Object;

            // Act
            var response = await _service.CreateAsync(invalidDto);

            // Assert
            Assert.True(response.HasError);
            Assert.Equal(400, response.StatusCode);

            _mockRepo.Verify(r => r.CreateAsync(It.IsAny<CreateUserDTO>()), Times.Never);
        }

        [Fact(DisplayName = "CreateAdminAsync com DTO válido deve chamar o repositório e retornar o resultado esperado")]
        public async Task CreateAdminAsync_ValidDto_CallsRepository_AndReturnsExpectedResult()
        {
            // Arrange
            var adminDto = new CreateUserAdminDTO
            {
                Name = "Admin Teste",
                Email = "admin@email.com",
                Password = "Senha@123"
            };

            // Act
            var response = await _service.CreateAdminAsync(adminDto);

            // Assert
            Assert.False(response.HasError);
            Assert.Equal(201, response.StatusCode);
            Assert.NotNull(response.Data);

            _mockRepo.Verify(r => r.CreateAsync(adminDto), Times.Once);
            _mockRepo.Verify(r => r.GetByIdAsync<ProjectUserDTO>(response.Data._id), Times.Once);
        }

        [Fact(DisplayName = "UpdateAsync deve chamar o repositório")]
        public async Task UpdateAsync_CallsRepository()
        {
            var updateDto = _fixture.Build<UpdateUserDTO>().Create();

            await _service!.UpdateAsync(ObjectId.Empty, updateDto);

            _mockRepo!.Verify(r => r.UpdateAsync(ObjectId.Empty, updateDto), Times.Once);
        }

        [Fact(DisplayName = "DeleteAsync deve chamar o repositório")]
        public async Task DeleteAsync_CallsRepository()
        {
            await _service!.DeleteAsync(ObjectId.Empty);

            _mockRepo!.Verify(r => r.DeleteAsync(ObjectId.Empty), Times.Once);
        }

        [Fact(DisplayName = "FindUsersAsync deve retornar usuários filtrados")]
        public async Task FindUsersAsync_ReturnsFilteredUsers()
        {
            // Arrange
            var filter = new FilterUserDTO { Name = _stubList[0].Name };

            _mockRepo.Setup(r => r.FindAsync<ProjectUserDTO>(filter))
                .ReturnsAsync(_stubList
                    .Where(u => u.Name == filter.Name)
                    .Select(u => new ProjectUserDTO(u))
                    .ToList());

            // Act
            var result = await _service.FindUsersAsync(filter);

            // Assert
            Assert.NotNull(result);
            Assert.All(result, u => Assert.Equal(filter.Name, u.Name));
            _mockRepo.Verify(r => r.FindAsync<ProjectUserDTO>(filter), Times.Once);
        }

        [Fact(DisplayName = "GetAdmin deve retornar o usuário administrador")]
        public async Task GetAdmin_ReturnsAdminUser()
        {
            // Arrange
            var adminUser = _stubList.First();
            adminUser.Role = Domain.Enums.UserRole.Admin;

            _mockRepo.Setup(r => r.FindOneAsync(It.IsAny<System.Linq.Expressions.Expression<Func<User, bool>>>()))
                .ReturnsAsync(adminUser);

            // Act
            var result = await _service.GetAdmin();

            // Assert
            Assert.NotNull(result);
            Assert.Equal(Domain.Enums.UserRole.Admin, result.Role);
            _mockRepo.Verify(r => r.FindOneAsync(It.IsAny<System.Linq.Expressions.Expression<Func<User, bool>>>()), Times.Once);
        }
    }
}
