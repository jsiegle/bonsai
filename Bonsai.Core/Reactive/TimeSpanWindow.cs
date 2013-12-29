﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Linq.Expressions;
using System.Reactive.Linq;
using System.Xml.Serialization;
using System.ComponentModel;
using System.Xml;
using System.Reflection;
using System.Reactive.Concurrency;

namespace Bonsai.Reactive
{
    [XmlType(Namespace = Constants.XmlNamespace)]
    [Description("Projects the sequence into non-overlapping windows of elements corresponding to the specified time interval.")]
    public class TimeSpanWindow : WindowCombinator
    {
        [XmlIgnore]
        [Description("The time interval of each window.")]
        public TimeSpan Length { get; set; }

        [Browsable(false)]
        [XmlElement("Length")]
        public string LengthXml
        {
            get { return XmlConvert.ToString(Length); }
            set { Length = XmlConvert.ToTimeSpan(value); }
        }

        public override IObservable<IObservable<TSource>> Process<TSource>(IObservable<TSource> source)
        {
            return source.Window(Length, HighResolutionScheduler.Default);
        }
    }
}