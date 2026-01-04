using Application.DTO.AuthenticationDTO;
using Application.Services;
using AutoFixture;
using Domain.Entities;
using Domain.Interfaces.Repositories;
using Domain.Interfaces.Services;
using Helpers.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using Moq;
using System.Linq.Expressions;
using Xunit;

namespace users_svc.Tests.ServiceTests
{
    public class AuthenticationServiceTests : BaseTests
    {
        private List<User> _stubUsers = null!;
        private Mock<IUserRepository> _mockUserRepo = null!;
        private Mock<IEventRepository> _mockEventRepo = null!;
        private Mock<IConfiguration> _mockConfiguration = null!;
        private Mock<IHostEnvironment> _mockEnv = null!;
        private IAuthenticationService _service = null!;

        protected override void InitStubs()
        {
            _stubUsers = new List<User>
            {
                new User
                {
                    _id = ObjectId.GenerateNewId(),
                    Name = "Existing User",
                    Email = "existing@email.com",
                    Password = "Senha@123".ToHash(), // Hash (mesma função do sistema)
                    Role = Domain.Enums.UserRole.UserApp
                }
            };
        }

        protected override void MockDependencies()
        {
            _mockUserRepo = new Mock<IUserRepository>(MockBehavior.Strict);
            _mockEventRepo = new Mock<IEventRepository>(MockBehavior.Strict);
            _mockConfiguration = new Mock<IConfiguration>();
            _mockEnv = new Mock<IHostEnvironment>();

            // Setup configuration para JWT
            SetupConfiguration();

            // Setup IEventRepository
            _mockEventRepo
                .Setup(e => e.AppendEventAsync(It.IsAny<DomainEvent>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Setup FindOneAsync
            _mockUserRepo.Setup(r => r.FindOneAsync(It.IsAny<Expression<Func<User, bool>>>()))
                .ReturnsAsync((Expression<Func<User, bool>> predicate) =>
                {
                    var compiled = predicate.Compile();
                    return _stubUsers.FirstOrDefault(compiled);
                });

            // Setup CreateAsync
            _mockUserRepo.Setup(r => r.CreateAsync(It.IsAny<RegisterUserDTO>()))
                .ReturnsAsync((RegisterUserDTO dto) =>
                {
                    var entity = new User
                    {
                        _id = ObjectId.GenerateNewId(),
                        Name = dto.Name,
                        Email = dto.Email,
                        Password = dto.Password.ToHash(),
                        Role = Domain.Enums.UserRole.UserApp
                    };
                    _stubUsers.Add(entity);
                    return entity;
                });

            _service = new AuthenticationService(
                _mockUserRepo.Object,
                _mockEventRepo.Object,
                _mockConfiguration.Object,
                _mockEnv.Object);
        }

        private void SetupConfiguration()
        {
            // Setup JWT configuration
            var jwtSection = new Mock<IConfigurationSection>();
            jwtSection.Setup(s => s.Value).Returns((string?)null);

            _mockConfiguration.Setup(c => c["JwtOptions:Key"])
                .Returns("quMjRLeWqR3Jp7jHWAaTlck1f1wOTavr");
            _mockConfiguration.Setup(c => c["JwtOptions:Issuer"])
                .Returns("test-issuer");
            _mockConfiguration.Setup(c => c["JwtOptions:Audience"])
                .Returns("test-audience");
            
            // SQS não configurado (retorna null)
            _mockConfiguration.Setup(c => c["Sqs:ServiceUrl"]).Returns((string?)null);
            _mockConfiguration.Setup(c => c["AWS:AccessKey"]).Returns((string?)null);
            _mockConfiguration.Setup(c => c["AWS:SecretKey"]).Returns((string?)null);
            _mockConfiguration.Setup(c => c["AWS:Region"]).Returns((string?)null);
            _mockConfiguration.Setup(c => c["Sqs:UsersEventsQueueUrl"]).Returns((string?)null);
            _mockConfiguration.Setup(c => c["USERS_EVENTS_QUEUE_URL"]).Returns((string?)null);
        }

        [Fact(DisplayName = "Register deve criar usuário com sucesso e registrar evento UserRegistered")]
        public async Task Register_ValidDto_CreatesUserAndPublishesEvent()
        {
            // Arrange
            var dto = new RegisterUserDTO
            {
                Name = "Novo Usuario",
                Email = "novo@email.com",
                Password = "Senha@123"
            };

            // Act
            var result = await _service.Register(dto);

            // Assert
            Assert.False(result.HasError);
            Assert.Equal(200, result.StatusCode);
            Assert.NotEqual(ObjectId.Empty, result.Data);

            _mockUserRepo.Verify(r => r.CreateAsync(dto), Times.Once);
            _mockEventRepo.Verify(e =>
                e.AppendEventAsync(It.Is<DomainEvent>(ev => ev.Type == "UserRegistered"),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact(DisplayName = "Register com DTO inválido deve retornar BadRequest e registrar evento de validação")]
        public async Task Register_InvalidDto_ReturnsBadRequest()
        {
            // Arrange
            var invalidDto = new RegisterUserDTO
            {
                Name = "",
                Email = "invalido",
                Password = ""
            };

            // Act
            var result = await _service.Register(invalidDto);

            // Assert
            Assert.True(result.HasError);
            Assert.Equal(400, result.StatusCode);

            _mockUserRepo.Verify(r => r.CreateAsync(It.IsAny<RegisterUserDTO>()), Times.Never);
            _mockEventRepo.Verify(e =>
                e.AppendEventAsync(It.Is<DomainEvent>(ev => ev.Type == "UserRegistrationValidationFailed"),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact(DisplayName = "Register com email duplicado deve retornar BadRequest e registrar evento")]
        public async Task Register_DuplicateEmail_ReturnsBadRequest()
        {
            // Arrange
            var dto = new RegisterUserDTO
            {
                Name = "Usuario Duplicado",
                Email = "existing@email.com", // Email já existe
                Password = "Senha@123"
            };

            // Act
            var result = await _service.Register(dto);

            // Assert
            Assert.True(result.HasError);
            Assert.Equal(400, result.StatusCode);
            Assert.Contains("já registrado", result.Message);

            _mockUserRepo.Verify(r => r.CreateAsync(It.IsAny<RegisterUserDTO>()), Times.Never);
            _mockEventRepo.Verify(e =>
                e.AppendEventAsync(It.Is<DomainEvent>(ev => ev.Type == "UserRegistrationDuplicateEmail"),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact(DisplayName = "Login com credenciais válidas deve retornar token e registrar evento UserLoggedIn")]
        public async Task Login_ValidCredentials_ReturnsToken()
        {
            // Arrange
            var dto = new LoginUserDTO
            {
                Email = "existing@email.com",
                Password = "Senha@123"
            };

            // Act
            var result = await _service.Login(dto);

            // Assert
            Assert.False(result.HasError);
            Assert.Equal(200, result.StatusCode);
            Assert.NotNull(result.Data);
            Assert.NotNull(result.Data.Token);
            Assert.NotNull(result.Data.UserInfo);

            _mockEventRepo.Verify(e =>
                e.AppendEventAsync(It.Is<DomainEvent>(ev => ev.Type == "UserLoggedIn"),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact(DisplayName = "Login com DTO inválido deve retornar BadRequest e registrar evento")]
        public async Task Login_InvalidDto_ReturnsBadRequest()
        {
            // Arrange
            var invalidDto = new LoginUserDTO
            {
                Email = "",
                Password = ""
            };

            // Act
            var result = await _service.Login(invalidDto);

            // Assert
            Assert.True(result.HasError);
            Assert.Equal(400, result.StatusCode);

            _mockEventRepo.Verify(e =>
                e.AppendEventAsync(It.Is<DomainEvent>(ev => ev.Type == "UserLoginValidationFailed"),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact(DisplayName = "Login com usuário inexistente deve retornar BadRequest e registrar evento")]
        public async Task Login_UserNotFound_ReturnsBadRequest()
        {
            // Arrange
            var dto = new LoginUserDTO
            {
                Email = "inexistente@email.com",
                Password = "Senha@123"
            };

            // Act
            var result = await _service.Login(dto);

            // Assert
            Assert.True(result.HasError);
            Assert.Equal(400, result.StatusCode);
            Assert.Contains("Inválido", result.Message);

            _mockEventRepo.Verify(e =>
                e.AppendEventAsync(It.Is<DomainEvent>(ev => ev.Type == "UserLoginUserNotFound"),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact(DisplayName = "Login com senha incorreta deve retornar Unauthorized e registrar evento UserLoginFailed")]
        public async Task Login_WrongPassword_ReturnsUnauthorized()
        {
            // Arrange
            var dto = new LoginUserDTO
            {
                Email = "existing@email.com",
                Password = "SenhaErrada@123"
            };

            // Act
            var result = await _service.Login(dto);

            // Assert
            Assert.True(result.HasError);
            Assert.Equal(401, result.StatusCode);

            _mockEventRepo.Verify(e =>
                e.AppendEventAsync(It.Is<DomainEvent>(ev => ev.Type == "UserLoginFailed"),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }
    }

}

