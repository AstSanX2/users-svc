using Application.DTO.AuthenticationDTO;
using Domain.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Controllers
{

    [ApiController]
    [AllowAnonymous]
    [Route("api/[controller]")]
    public class AuthenticationController(IAuthenticationService service) : ControllerBase
    {
        [HttpPost("Login")]
        public async Task<IActionResult> Login([FromBody] LoginUserDTO userLoginRequest)
        {
            var loginResult = await service.Login(userLoginRequest);
            var response = loginResult.GetResponse();

            if (loginResult.StatusCode is 200)
                return Ok(response);
            else if (loginResult.StatusCode is 401)
                return Unauthorized(response);

            return BadRequest(response);
        }


        [HttpPost("Register")]
        public async Task<IActionResult> Register([FromBody] RegisterUserDTO userRegisterRequest)
        {
            var registerResult = await service.Register(userRegisterRequest);
            var response = registerResult.GetResponse();

            if (!registerResult.HasError)
                return CreatedAtAction(nameof(Register), response);

            return BadRequest(registerResult);
        }
    }
}
