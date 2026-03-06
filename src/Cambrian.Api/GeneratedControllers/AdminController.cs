using Microsoft.AspNetCore.Mvc;

namespace Cambrian.Api.Controllers;

[ApiController]
[Route("")]
public class AdminController : ControllerBase
{

        [HttpGet("admin/audit")]
        public IActionResult GET_admin_audit()
        {
            return Ok("stub");
        }

        [HttpGet("admin/dashboard")]
        public IActionResult GET_admin_dashboard()
        {
            return Ok("stub");
        }

        [HttpGet("admin/settings")]
        public IActionResult GET_admin_settings()
        {
            return Ok("stub");
        }

        [HttpPost("admin/settings")]
        public IActionResult POST_admin_settings()
        {
            return Ok("stub");
        }

        [HttpGet("admin/payouts/requests")]
        public IActionResult GET_admin_payouts_requests()
        {
            return Ok("stub");
        }

        [HttpPost("admin/payouts/{id}/approve")]
        public IActionResult POST_admin_payouts_id_approve()
        {
            return Ok("stub");
        }

        [HttpPost("admin/payouts/{id}/reject")]
        public IActionResult POST_admin_payouts_id_reject()
        {
            return Ok("stub");
        }

        [HttpPost("admin/users/{id}/role")]
        public IActionResult POST_admin_users_id_role()
        {
            return Ok("stub");
        }

        [HttpGet("admin/users")]
        public IActionResult GET_admin_users()
        {
            return Ok("stub");
        }

        [HttpPost("admin/users/{id}/suspend")]
        public IActionResult POST_admin_users_id_suspend()
        {
            return Ok("stub");
        }

        [HttpPost("admin/users/{id}/reactivate")]
        public IActionResult POST_admin_users_id_reactivate()
        {
            return Ok("stub");
        }

        [HttpPost("admin/users/{id}/reset-password")]
        public IActionResult POST_admin_users_id_reset_password()
        {
            return Ok("stub");
        }

        [HttpPost("admin/users/{id}/verify-creator")]
        public IActionResult POST_admin_users_id_verify_creator()
        {
            return Ok("stub");
        }

        [HttpGet("admin/reports")]
        public IActionResult GET_admin_reports()
        {
            return Ok("stub");
        }

        [HttpPost("admin/reports/{id}/investigate")]
        public IActionResult POST_admin_reports_id_investigate()
        {
            return Ok("stub");
        }

        [HttpPost("admin/tracks/{id}/remove")]
        public IActionResult POST_admin_tracks_id_remove()
        {
            return Ok("stub");
        }

        [HttpPost("admin/tracks/{id}/restore")]
        public IActionResult POST_admin_tracks_id_restore()
        {
            return Ok("stub");
        }

        [HttpPost("admin/tracks/{id}/hide")]
        public IActionResult POST_admin_tracks_id_hide()
        {
            return Ok("stub");
        }

        [HttpPost("admin/tracks/{id}/flag")]
        public IActionResult POST_admin_tracks_id_flag()
        {
            return Ok("stub");
        }

        [HttpPost("admin/tracks/{id}/feature")]
        public IActionResult POST_admin_tracks_id_feature()
        {
            return Ok("stub");
        }

        [HttpPost("admin/tracks/{id}/pin")]
        public IActionResult POST_admin_tracks_id_pin()
        {
            return Ok("stub");
        }

        [HttpPost("admin/tracks/{id}/visibility")]
        public IActionResult POST_admin_tracks_id_visibility()
        {
            return Ok("stub");
        }

        [HttpPost("admin/collections/curate")]
        public IActionResult POST_admin_collections_curate()
        {
            return Ok("stub");
        }

        [HttpPost("admin/tags/manage")]
        public IActionResult POST_admin_tags_manage()
        {
            return Ok("stub");
        }
}
