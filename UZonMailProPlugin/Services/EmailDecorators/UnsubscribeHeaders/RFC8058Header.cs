﻿using MimeKit;

namespace UZonMailProPlugin.Services.EmailDecorators.UnsubscribeHeaders
{
    public class RFC8058Header : IUnsubscribeHeader
    {
        public virtual void SetHeader(MimeMessage mimeMessage, string unsubscribeUrl)
        {
            mimeMessage.Headers.Add(HeaderId.ListUnsubscribePost, "List-Unsubscribe=One-Click");
            if (!string.IsNullOrEmpty(unsubscribeUrl))
                mimeMessage.Headers.Add(HeaderId.ListUnsubscribe, $"<{unsubscribeUrl}>");
        }
    }
}
