using FundDca.Api.Models;
using FundDca.Api.Services;
using FundDca.Import.Parsing;
using Microsoft.AspNetCore.Mvc;

namespace FundDca.Api.Controllers;

[ApiController]
[Route("api/import")]
public class ImportController(ImportService imports) : ControllerBase
{
    /// <summary>使用内置演示资产证明解析并对账（不走文件上传）。</summary>
    [HttpPost("parse-demo")]
    public async Task<IActionResult> ParseDemo(CancellationToken ct)
    {
        try
        {
            return Ok(await imports.ParseDemoAsync(ct));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>上传资产证明（PDF/XLSX/CSV），本地解析并返回对账预览（不落库）。</summary>
    [HttpPost("parse")]
    public async Task<IActionResult> Parse(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { message = "请选择要上传的资产证明文件" });
        }

        try
        {
            using var stream = file.OpenReadStream();
            var preview = await imports.ParseUploadAsync(stream, file.FileName, ct);
            return Ok(preview);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>确认导入：按用户对每条差异的选择，事务应用并落持仓版本快照。</summary>
    [HttpPost("commit")]
    public async Task<IActionResult> Commit([FromBody] CommitRequestDto body, CancellationToken ct)
    {
        try
        {
            return Ok(await imports.CommitAsync(body, ct));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>持仓版本列表（回滚入口）。</summary>
    [HttpGet("versions")]
    public async Task<IReadOnlyList<VersionListItemDto>> Versions(CancellationToken ct) =>
        await imports.ListVersionsAsync(ct);

    /// <summary>一键回滚到指定持仓版本。</summary>
    [HttpPost("versions/{id:long}/rollback")]
    public async Task<IActionResult> Rollback(long id, CancellationToken ct)
    {
        try
        {
            return Ok(await imports.RollbackAsync(id, ct));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>下载标准 Excel 导入模板（PDF 版式无法识别时的兜底）。</summary>
    [HttpGet("template")]
    public IActionResult Template()
    {
        var bytes = StandardTemplateBuilder.Build();
        return File(
            bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            StandardTemplateBuilder.FileName);
    }
}
