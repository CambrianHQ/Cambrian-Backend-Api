using Microsoft.AspNetCore.Identity;
using Npgsql;

var connStr = "Host=localhost;Port=5432;Database=cambrian;Username=cambrian;Password=cambrian";
using var conn = new NpgsqlConnection(connStr);
conn.Open();

// Hash the new password using ASP.NET Identity's PasswordHasher
var hasher = new PasswordHasher<string>();
var newPassword = "Admin1234!";
var hash = hasher.HashPassword("testuser1@cambrian.dev", newPassword);

Console.WriteLine($"Resetting password for testuser1@cambrian.dev to: {newPassword}");

using var cmd = new NpgsqlCommand(@"UPDATE ""AspNetUsers"" SET ""PasswordHash"" = @hash WHERE ""Email"" = 'testuser1@cambrian.dev';", conn);
cmd.Parameters.AddWithValue("hash", hash);
int rows = cmd.ExecuteNonQuery();
Console.WriteLine($"Updated {rows} row(s).");
Console.WriteLine("Done.");
