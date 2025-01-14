﻿using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using WayfJwtConnector.Extensions;
using WayfJwtConnector.Helpers;
using WayfJwtConnector.Models;

namespace WayfJwtConnector
{
    public class WayfClient
    {
        private readonly ReadOnlyDictionary<string, string> defaultQuery;
        private readonly IOptions<WayfOptions> _options;
        private readonly WayfHttpClientFactory _factory;
        private readonly SecurityKey _key;

        public WayfClient(WayfHttpClientFactory httpClient, IOptions<WayfOptions> options)
        {
            _options = options;
            _key = RsaHelper.IssuerSigningKey(_options.Value.WayfPublicKey);
            defaultQuery = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>
            {
                ["acs"] = _options.Value.Acs,
                ["issuer"] = _options.Value.Issuer,
            });
            _factory = httpClient;
        }
        public ValueTask<string> NemLogin()
        {
            var scoping = "https://nemlogin.wayf.dk";
            return RedirectUrl(scoping);
        }

        public async ValueTask<string> RedirectUrl(string scoping = "")
        {
            var query = new Dictionary<string, string>(defaultQuery)
            {
                { "idplist", string.Join(",", scoping) }
            };

            var url = QueryHelpers.AddQueryString(_options.Value.Endpoint, query);
            var data = await _factory.Client.GetAsync(url);
            var redirectUrl = data.RequestMessage.RequestUri.ToString();
            return redirectUrl;
        }

        /// <summary>
        /// Validates the data recieved from the client
        /// Called with Response.Body 
        /// </summary>
        /// <param name="requestBody"></param>
        /// <returns></returns>
        public async ValueTask<WayfClaims> ValidateAsync(Stream requestBody)
        {
            HttpResponseMessage response = await ExchangeTokens(requestBody);

            if (response.Headers.TryGetValues("Authorization", out var values))
            {
                var token = values.FirstOrDefault()?.Replace("Bearer ", "").Trim();

                if (string.IsNullOrEmpty(token))
                {
                    throw new Exception("Authorization header invalid");
                }

                var principal = ValidateToken(token);
                return BuildContainer(principal);
            }
            else
            {
                throw new Exception("No Authorization header received from wayf");
            }
        }

        private ClaimsPrincipal ValidateToken(string token)
        {
            var validationParameters = new TokenValidationParameters()
            {
                RequireExpirationTime = false,
                RequireSignedTokens = true,
                ValidateAudience = false,
                ValidateIssuer = false,
                IssuerSigningKey = _key,
            };
            var handler = new JwtSecurityTokenHandler();
            var principal = handler.ValidateToken(token, validationParameters, out _);
            return principal;
        }

        private WayfClaims BuildContainer(ClaimsPrincipal principal)
        {
            var container = new WayfClaims()
            {
                FullName = principal.FindFirstOrEmpty(r => r.Type == "cn"),
                EduPersonPrimaryAffiliation = principal.FindFirstOrEmpty(r => r.Type == "eduPersonPrimaryAffiliation"),
                UserId = principal.FindFirstOrEmpty(r => r.Type == "uid"),
            };
            return container;
        }

        private async ValueTask<HttpResponseMessage> ExchangeTokens(Stream requestBody)
        {
            var body = await ReadBody(requestBody);

            var content = defaultQuery
                .Select(r => $"{HttpUtility.UrlEncode(r.Key)}={HttpUtility.UrlEncode(r.Value)}")
                .Append(body);
            var query = string.Join("&", content);

            var wayfRequest = new StringContent(query, Encoding.UTF8, "application/x-www-form-urlencoded");

            var response = await _factory.Client.PostAsync(_options.Value.Endpoint, wayfRequest);
            return response;
        }

        private async ValueTask<string> ReadBody(Stream requestBody)
        {
            StringBuilder builder = new StringBuilder();
            byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);
            while (true)
            {
                var bytesRemaining = await requestBody.ReadAsync(buffer, offset: 0, buffer.Length);
                if (bytesRemaining == 0)
                {
                    break;
                }
                var encodedString = Encoding.UTF8.GetString(buffer, 0, bytesRemaining);
                builder.Append(encodedString);
            }
            ArrayPool<byte>.Shared.Return(buffer);

            var entireRequestBody = builder.ToString();
            return entireRequestBody;
        }
    }
}
