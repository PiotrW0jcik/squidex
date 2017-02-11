﻿// ==========================================================================
//  FieldEvent.cs
//  Squidex Headless CMS
// ==========================================================================
//  Copyright (c) Squidex Group
//  All rights reserved.
// ==========================================================================

using Squidex.Infrastructure;

namespace Squidex.Events.Schemas
{
    public abstract class FieldEvent : SchemaEvent
    {
        public NamedId<long> FieldId { get; set; }
    }
}
