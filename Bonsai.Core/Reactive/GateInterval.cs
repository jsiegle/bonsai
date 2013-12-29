﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Linq.Expressions;
using System.Reflection;
using System.Reactive.Linq;
using System.Xml.Serialization;
using System.ComponentModel;
using System.Xml;

namespace Bonsai.Reactive
{
    [XmlType(Namespace = Constants.XmlNamespace)]
    [Description("Allows the next value of the sequence to propagate only when the specified interval elapses.")]
    public class GateInterval : Combinator
    {
        [XmlIgnore]
        [Description("The time interval after which a new value of the sequence is allowed to propagate.")]
        public TimeSpan Interval { get; set; }

        [Browsable(false)]
        [XmlElement("Interval")]
        public string IntervalXml
        {
            get { return XmlConvert.ToString(Interval); }
            set { Interval = XmlConvert.ToTimeSpan(value); }
        }

        public override IObservable<TSource> Process<TSource>(IObservable<TSource> source)
        {
            return source.Gate(Interval, HighResolutionScheduler.Default);
        }
    }
}