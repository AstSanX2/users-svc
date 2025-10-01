using Domain.Interfaces.Repositories;
using Domain.Entities;
using MongoDB.Driver;

namespace Infraestructure.Repositories
{
    public class UserRepository(IMongoDatabase database) : BaseRepository<User>(database), IUserRepository
    {
    }
}
