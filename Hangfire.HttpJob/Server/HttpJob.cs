﻿using Hangfire.Console;
using Hangfire.Logging;
using Hangfire.Server;
using Newtonsoft.Json;
using System;
using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using MailKit;
using MimeKit;
using MailKit.Net.Smtp;

namespace Hangfire.HttpJob.Server
{
    internal class HttpJob
    {
        private static readonly ILog Logger = LogProvider.For<HttpJob>();
        public static HangfireHttpJobOptions HangfireHttpJobOptions;
        private static MimeMessage mimeMessage;


        public static HttpClient GetHttpClient(HttpJobItem item)
        {
            var handler = new HttpClientHandler();
            if (HangfireHttpJobOptions.Proxy == null)
            {
                handler.UseProxy = false;
            }
            else
            {
                handler.Proxy = HangfireHttpJobOptions.Proxy;
            }

            var HttpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMilliseconds(item.Timeout == 0 ? HangfireHttpJobOptions.GlobalHttpTimeOut : item.Timeout),
            };

            if (!string.IsNullOrEmpty(item.BasicUserName) && !string.IsNullOrEmpty(item.BasicPassword))
            {
                var byteArray = Encoding.ASCII.GetBytes(item.BasicUserName + ":" + item.BasicPassword);
                HttpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));

            }
            return HttpClient;
        }

        public static HttpRequestMessage PrepareHttpRequestMessage(HttpJobItem item)
        {
            var request = new HttpRequestMessage(new HttpMethod(item.Method), item.Url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(item.ContentType));
            if (!item.Method.ToLower().Equals("get"))
            {
                if (!string.IsNullOrEmpty(item.Data))
                {
                    var bytes = Encoding.UTF8.GetBytes(item.Data);
                    request.Content = new ByteArrayContent(bytes, 0, bytes.Length);
                }
            }
            return request;
        }


        [AutomaticRetry(Attempts = 3)]
        [DisplayName("Api任务:{1}")]
        [Queue("apis")]
        public static void Excute(HttpJobItem item, string jobName = null, PerformContext context = null)
        {
            try
            {
                mimeMessage = new MimeMessage();
                mimeMessage.From.Add(new MailboxAddress(HangfireHttpJobOptions.SendMailAddress));
                HangfireHttpJobOptions.SendToMailList.ForEach(k =>
                {
                    mimeMessage.To.Add(new MailboxAddress(k));
                });
                mimeMessage.Subject = HangfireHttpJobOptions.SMTPSubject;
            }
            catch (Exception ee)
            {
                context.SetTextColor(ConsoleTextColor.Red);
                context.WriteLine($"邮件服务异常，异常为：{ee}");
            }
            
            try
            {
                //此处信息会显示在执行结果日志中
                context.SetTextColor(ConsoleTextColor.Yellow);
                context.WriteLine("任务开始执行");
                context.WriteLine($"{DateTime.Now.ToString()}");
                context.WriteLine(jobName);
                context.WriteLine(JsonConvert.SerializeObject(item));
                var client = GetHttpClient(item);
                var httpMesage = PrepareHttpRequestMessage(item);
                var httpResponse = client.SendAsync(httpMesage).GetAwaiter().GetResult();
                HttpContent content = httpResponse.Content;
                string result = content.ReadAsStringAsync().GetAwaiter().GetResult();
                context.WriteLine($"执行结果：{result}");
            }
            catch (Exception ex)
            {
                context.SetTextColor(ConsoleTextColor.Red);
                var builder = new BodyBuilder();
                builder.TextBody = $"执行出错,任务名称【{item.JobName}】,错误详情：{ex}";
                mimeMessage.Body = builder.ToMessageBody();
                var client = new SmtpClient();
                client.Connect(HangfireHttpJobOptions.SMTPServerAddress, HangfireHttpJobOptions.SMTPPort, true);     //连接服务
                client.AuthenticationMechanisms.Remove("XOAUTH2");
                client.Authenticate(HangfireHttpJobOptions.SendMailAddress, HangfireHttpJobOptions.SMTPPwd); //验证账号密码
                client.Send(mimeMessage);
                client.Disconnect(true);
                Logger.ErrorException("HttpJob.Excute", ex);
                context.WriteLine($"执行出错：{ex.Message}");
            }
        }

    }



}