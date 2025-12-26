using Microsoft.AspNetCore.Mvc;

namespace CarterCam.API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class VideoController : Controller
    {
        [HttpPost("upload")]
        public async Task<IActionResult> UploadFrame()
        {
            using var ms = new MemoryStream();
            await Request.Body.CopyToAsync(ms);
            byte[] frameData = ms.ToArray();

            if (frameData.Length == 0)
                return BadRequest("No frame data received.");

            // TODO Forward to C++ ingestion engine

            Console.WriteLine(frameData.Length);
            return Ok(new { message = "Frame received successfully.", size = frameData.Length });
        }

        [HttpGet("test")]
        public IActionResult Test()
        {
            return Ok("Test endpoint reached.");
        }
    }
}
