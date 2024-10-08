﻿using log4net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using Uamazing.Utils.Web.ResponseModel;
using UZonMail.Core.Config;
using UZonMail.Core.Services.Permission;
using UZonMail.Core.Services.Settings;
using UZonMail.DB.SQL;
using UZonMail.DB.SQL.Unsubscribes;
using UZonMail.Managers.Cache;
using UZonMail.Utils.Json;
using UZonMail.Utils.Results;
using UZonMail.Utils.Web.ResponseModel;
using UZonMail.Utils.Web.Token;
using UZonMailProPlugin.Controllers.Base;
using UZonMailProPlugin.Controllers.Unsubscribes.ResponseModels;
using UZonMailProPlugin.Services.EmailBodyDecorators;
using UZonMailProPlugin.Services.Unsubscribe;

namespace UZonMailProPlugin.Controllers.Unsubscribes
{
    /// <summary>
    /// 退订控制器
    /// 退订默认是组织级别的设置
    /// </summary>
    public class UnsubscribeController(SqlContext db, TokenService tokenService, IConfiguration configuration, UnsubscribeService unsubscribeService
        , PermissionService permissionService
        ) : ControllerBasePro
    {
        private static readonly ILog _logger = LogManager.GetLogger(typeof(UnsubscribeController));

        /// <summary>
        /// 只有管理员可以获取退订设置
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        public async Task<ResponseResult<UnsubscribeSetting>> GetUnsubscribeSettings()
        {
            var userId = tokenService.GetUserDataId();
            // 判断是否是组织管理员
            var hasOrgAdmin = await permissionService.HasOrganizationPermission(userId);
            if(!hasOrgAdmin)
            {
                return ResponseResult<UnsubscribeSetting>.Fail("You are not organization admin");
            }

            // 获取管理员的组织
            var organizationId = tokenService.GetOrganizationId();
            var unsubscribeSetting = await db.UnsubscribeSettings.FirstOrDefaultAsync(x => x.OrganizationId == organizationId);
            if (unsubscribeSetting == null)
            {
                unsubscribeSetting = new UnsubscribeSetting()
                {
                    OrganizationId = organizationId,
                };
                db.Add(unsubscribeSetting);
                await db.SaveChangesAsync();
            }

            return unsubscribeSetting.ToSuccessResponse();
        }

        /// <summary>
        /// 只有管理员可以管理退订设置
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        [HttpPut("{settingId:long}")]
        public async Task<ResponseResult<bool>> UpdateUnsubscribeSettings(long settingId, [FromBody] UnsubscribeSetting data)
        {
            var userId = tokenService.GetUserDataId();
            // 判断是否是组织管理员
            var hasOrgAdmin = await permissionService.HasOrganizationPermission(userId);
            if (!hasOrgAdmin)
            {
                return ResponseResult<bool>.Fail("You are not organization admin");
            }

            var organizationId = tokenService.GetOrganizationId();
            data.OrganizationId = organizationId;

            var exist = await db.UnsubscribeSettings.FirstOrDefaultAsync(x => x.Id == settingId && x.OrganizationId == organizationId);
            if (exist == null)
            {
                db.Add(data);
                exist = data;
            }
            else
            {
                // 更新
                exist.Enable = data.Enable;
                exist.Type = data.Type;
                exist.ExternalUrl = data.ExternalUrl;
            }
            await db.SaveChangesAsync();

            // 更新缓存
            CacheManager.UpdateCache<UnsubscribeSettingsReader>(exist.ObjectId);

            return true.ToSuccessResponse();
        }

        /// <summary>
        /// 获取退订状态
        /// </summary>
        /// <param name="token"></param>
        /// <returns></returns>
        [AllowAnonymous]
        [HttpGet("status")]
        public async Task<ResponseResult<bool>> ChangeUnsubscribeStatus(string token)
        {
            var tokenParams = new TokenParams();
            configuration.GetSection("UnsubscribeTokenParams").Bind(tokenParams);
            if (string.IsNullOrEmpty(tokenParams.Secret))
            {
                _logger.Error("系统未配置取消订阅密钥，用户退定失败");
                return ResponseResult<bool>.Fail("系统未配置取消订阅密钥");
            }

            // 从 token 中解析出 email 和 organizationId
            var tokenPayloads = tokenParams.GetTokenPayloads(token);
            // 获取 email
            var email = tokenPayloads.SelectTokenOrDefault("email", string.Empty);
            var organizationId = tokenPayloads.SelectTokenOrDefault("organizationId", "0");
            var longOrganizationId = long.Parse(organizationId);
            if (string.IsNullOrEmpty(email) || longOrganizationId == 0)
            {
                var message = "无法解析退订 token";
                _logger.Error(message);
                return ResponseResult<bool>.Fail(message);
            }

            // 添加到退订列表
            var existOne = await db.UnsubscribeEmails.FirstOrDefaultAsync(x => x.Email == email && x.OrganizationId == longOrganizationId);
            return (existOne != null).ToSuccessResponse();
        }

        /// <summary>
        /// 解析邮件退订中 token 的 payload
        /// 若退订使用外部链接，可以调用该接口获取 token 中的 email 和 organizationId
        /// </summary>
        /// <param name="token"></param>
        /// <returns></returns>
        [HttpGet("token-payloads")]
        public async Task<ResponseResult<UnsubscribePayloads>> GetTokenPayloads(string token)
        {
            // token 是 SendItem 中的 ObjectId
            var sendingItem = await db.SendingItems.AsNoTracking().FirstOrDefaultAsync(x => x.ObjectId == token);
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sendingItem.UserId);

            // 从 token 中解析出 email 和 organizationId
            var tokenPayloads = new UnsubscribePayloads(sendingItem)
            {
                OrganizationId = user.OrganizationId
            };
            return tokenPayloads.ToSuccessResponse();
        }

        /// <summary>
        /// 取消订阅
        /// </summary>
        /// <param name="token"></param>
        /// <returns></returns>
        [AllowAnonymous]
        [HttpPost()]
        public async Task<ResponseResult<bool>> Unsubscribe(string token)
        {
            var result = await unsubscribeService.Unsubscribe(token);
            // 开始
            return result.ToSuccessResponse();
        }

        /// <summary>
        /// 是否已经取消订阅
        /// </summary>
        /// <param name="token"></param>
        /// <returns></returns>
        [AllowAnonymous]
        [HttpGet("unsubscribed")]
        public async Task<ResponseResult<bool>> IsUnsubscribed(string token)
        {
            var result = await unsubscribeService.IsUnsubscribed(token);
            return result.ToSuccessResponse();
        }
    }
}