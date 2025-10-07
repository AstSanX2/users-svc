using Application.DTO.UsersDTO;
using Application.Services;
using AutoFixture;
using Domain.Entities;
using Domain.Interfaces.Repositories;
using Domain.Interfaces.Services;
using MongoDB.Bson;
using Moq;
using System.Linq.Expressions;

namespace users_svc.Tests.ServiceTests
{
    public class UserServiceTests : BaseTests
    {
        private List<User> _stubList = null!;
        private Mock<IUserRepository> _mockRepo = null!;
        private Mock<IEventRepository> _mockEventRepo = null!;
        private IUserService _service = null!;

        protected override void InitStubs()
        {
            _stubList = _fixture.Build<User>()
                                .With(e => e._id, ObjectId.GenerateNewId())
                                .CreateMany(2)
                                .ToList();
        }

        protected override void MockDependencies()
        {
            _mockRepo = new Mock<IUserRepository>(MockBehavior.Strict);
            _mockEventRepo = new Mock<IEventRepository>(MockBehavior.Strict);

            _mockEventRepo
                .Setup(e => e.AppendEventAsync(It.IsAny<DomainEvent>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            _mockRepo.Setup(r => r.GetAllAsync<ProjectUserDTO>())
                .ReturnsAsync(_stubList.Select(x => new ProjectUserDTO(x)).ToList());

            _mockRepo.Setup(r => r.GetByIdAsync<ProjectUserDTO>(It.IsAny<ObjectId>()))
                .ReturnsAsync((ObjectId id) =>
                {
                    var user = _stubList.FirstOrDefault(x => x._id == id);
                    return user == null ? null : new ProjectUserDTO(user);
                });

            _mockRepo.Setup(r => r.CreateAsync(It.IsAny<CreateUserDTO>()))
                .ReturnsAsync((CreateUserDTO dto) =>
                {
                    var entity = dto.ToEntity();
                    entity._id = ObjectId.GenerateNewId();
                    _stubList.Add(entity);
                    return entity;
                });

            _mockRepo.Setup(r => r.CreateAsync(It.IsAny<CreateUserAdminDTO>()))
                .ReturnsAsync((CreateUserAdminDTO dto) =>
                {
                    var entity = dto.ToEntity();
                    entity._id = ObjectId.GenerateNewId();
                    _stubList.Add(entity);
                    return entity;
                });

            _mockRepo.Setup(r => r.UpdateAsync(It.IsAny<ObjectId>(), It.IsAny<UpdateUserDTO>()))
                .Returns(Task.CompletedTask);

            _mockRepo.Setup(r => r.DeleteAsync(It.IsAny<ObjectId>()))
                .Callback<ObjectId>(id =>
                {
                    var index = _stubList.FindIndex(x => x._id == id);
                    if (index >= 0) _stubList.RemoveAt(index);
                })
                .Returns(Task.CompletedTask);

            _service = new UserService(_mockRepo.Object, _mockEventRepo.Object);
        }

        [Fact(DisplayName = "GetAllAsync deve retornar todas as entidades e registrar evento UsersListed")]
        public async Task GetAllAsync_ReturnsEntities_AndPublishesEvent()
        {
            var result = await _service.GetAllAsync();

            Assert.NotNull(result);
            Assert.Equal(_stubList.Count, result.Count);

            _mockEventRepo.Verify(e =>
                e.AppendEventAsync(It.Is<DomainEvent>(ev => ev.Type == "UsersListed"),
                                   It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact(DisplayName = "GetByIdAsync deve retornar a entidade correspondente e registrar UserFetched")]
        public async Task GetByIdAsync_ReturnsEntity_AndPublishesFetched()
        {
            var item = _fixture.Build<User>()
                               .With(e => e._id, ObjectId.GenerateNewId)
                               .Create();
            _stubList.Add(item);

            var result = await _service.GetByIdAsync(item._id);

            Assert.NotNull(result);
            Assert.Equal(item._id, result!._id);

            _mockEventRepo.Verify(e =>
                e.AppendEventAsync(It.Is<DomainEvent>(ev =>
                    ev.Type == "UserFetched" && ev.AggregateId == item._id),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact(DisplayName = "GetByIdAsync (not found) deve registrar UserNotFound")]
        public async Task GetByIdAsync_NotFound_PublishesNotFound()
        {
            var missing = ObjectId.GenerateNewId();

            var result = await _service.GetByIdAsync(missing);

            Assert.Null(result);

            _mockEventRepo.Verify(e =>
                e.AppendEventAsync(It.Is<DomainEvent>(ev =>
                    ev.Type == "UserNotFound" && ev.AggregateId == missing),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact(DisplayName = "CreateAsync deve chamar repositório, retornar Created e registrar UserCreated")]
        public async Task CreateAsync_CallsRepository_ReturnsCreated_PublishesEvent()
        {
            var dto = new CreateUserDTO
            {
                Name = "Usuário Teste",
                Email = "teste@email.com",
                Password = "Senha@123"
            };

            var response = await _service.CreateAsync(dto);

            Assert.False(response.HasError);
            Assert.Equal(201, response.StatusCode);
            Assert.NotNull(response.Data);

            _mockRepo.Verify(r => r.CreateAsync(dto), Times.Once);
            _mockRepo.Verify(r => r.GetByIdAsync<ProjectUserDTO>(response.Data._id), Times.Once);

            _mockEventRepo.Verify(e =>
                e.AppendEventAsync(It.Is<DomainEvent>(ev => ev.Type == "UserCreated"), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact(DisplayName = "CreateAsync com DTO inválido deve retornar BadRequest e registrar UserCreateValidationFailed")]
        public async Task CreateAsync_InvalidDto_ReturnsBadRequest_PublishesValidationFailed()
        {
            var invalidDto = new CreateUserDTO
            {
                // Campos vazios/invalidos para forçar falha de validação
                Name = "",
                Email = "invalido",
                Password = ""
            };

            var response = await _service.CreateAsync(invalidDto);

            Assert.True(response.HasError);
            Assert.Equal(400, response.StatusCode);

            _mockRepo.Verify(r => r.CreateAsync(It.IsAny<CreateUserDTO>()), Times.Never);

            _mockEventRepo.Verify(e =>
                e.AppendEventAsync(It.Is<DomainEvent>(ev => ev.Type == "UserCreateValidationFailed"),
                                   It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact(DisplayName = "CreateAdminAsync deve criar admin e registrar UserCreated")]
        public async Task CreateAdminAsync_ValidDto_CreatesAndPublishes()
        {
            var adminDto = new CreateUserAdminDTO
            {
                Name = "Admin Teste",
                Email = "admin@email.com",
                Password = "Senha@123"
            };

            var response = await _service.CreateAdminAsync(adminDto);

            Assert.False(response.HasError);
            Assert.Equal(201, response.StatusCode);
            Assert.NotNull(response.Data);

            _mockRepo.Verify(r => r.CreateAsync(adminDto), Times.Once);
            _mockRepo.Verify(r => r.GetByIdAsync<ProjectUserDTO>(response.Data._id), Times.Once);

            _mockEventRepo.Verify(e =>
                e.AppendEventAsync(It.Is<DomainEvent>(ev => ev.Type == "UserCreated"), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact(DisplayName = "UpdateAsync deve chamar repositório e registrar UserUpdated")]
        public async Task UpdateAsync_CallsRepository_PublishesUpdated()
        {
            var id = _stubList[0]._id;
            var updateDto = _fixture.Build<UpdateUserDTO>().Create();

            await _service.UpdateAsync(id, updateDto);

            _mockRepo.Verify(r => r.UpdateAsync(id, updateDto), Times.Once);

            _mockEventRepo.Verify(e =>
                e.AppendEventAsync(It.Is<DomainEvent>(ev =>
                    ev.Type == "UserUpdated" && ev.AggregateId == id),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact(DisplayName = "DeleteAsync deve chamar repositório e registrar UserDeleted")]
        public async Task DeleteAsync_CallsRepository_PublishesDeleted()
        {
            var id = _stubList[0]._id;

            await _service.DeleteAsync(id);

            _mockRepo.Verify(r => r.DeleteAsync(id), Times.Once);

            _mockEventRepo.Verify(e =>
                e.AppendEventAsync(It.Is<DomainEvent>(ev =>
                    ev.Type == "UserDeleted" && ev.AggregateId == id),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact(DisplayName = "FindUsersAsync deve retornar usuários filtrados e registrar UserFilterQueried")]
        public async Task FindUsersAsync_ReturnsFiltered_AndPublishesEvent()
        {
            var filter = new FilterUserDTO { Name = _stubList[0].Name };

            _mockRepo.Setup(r => r.FindAsync<ProjectUserDTO>(filter))
                .ReturnsAsync(_stubList
                    .Where(u => u.Name == filter.Name)
                    .Select(u => new ProjectUserDTO(u))
                    .ToList());

            var result = await _service.FindUsersAsync(filter);

            Assert.NotNull(result);
            Assert.All(result, u => Assert.Equal(filter.Name, u.Name));
            _mockRepo.Verify(r => r.FindAsync<ProjectUserDTO>(filter), Times.Once);

            _mockEventRepo.Verify(e =>
                e.AppendEventAsync(It.Is<DomainEvent>(ev => ev.Type == "UserFilterQueried"),
                                   It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact(DisplayName = "GetAdmin deve retornar admin e registrar AdminFetched")]
        public async Task GetAdmin_ReturnsAdmin_PublishesFetched()
        {
            var adminUser = _stubList.First();
            adminUser.Role = Domain.Enums.UserRole.Admin;

            _mockRepo.Setup(r => r.FindOneAsync(It.IsAny<Expression<Func<User, bool>>>()))
                .ReturnsAsync(adminUser);

            var result = await _service.GetAdmin();

            Assert.NotNull(result);
            Assert.Equal(Domain.Enums.UserRole.Admin, result.Role);
            _mockRepo.Verify(r => r.FindOneAsync(It.IsAny<Expression<Func<User, bool>>>()), Times.Once);

            _mockEventRepo.Verify(e =>
                e.AppendEventAsync(It.Is<DomainEvent>(ev => ev.Type == "AdminFetched"),
                                   It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact(DisplayName = "GetAdmin (not found) deve registrar AdminNotFound")]
        public async Task GetAdmin_NotFound_PublishesNotFound()
        {
            _mockRepo.Setup(r => r.FindOneAsync(It.IsAny<Expression<Func<User, bool>>>()))
                .ReturnsAsync((User?)null);

            var result = await _service.GetAdmin();

            Assert.Null(result);

            _mockEventRepo.Verify(e =>
                e.AppendEventAsync(It.Is<DomainEvent>(ev => ev.Type == "AdminNotFound"),
                                   It.IsAny<CancellationToken>()),
                Times.Once);
        }
    }
}
