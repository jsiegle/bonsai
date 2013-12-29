﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;

namespace Bonsai.Expressions
{
    /// <summary>
    /// Represents a workflow property containing a timestamp in Coordinated Universal Time (UTC).
    /// </summary>
    [DisplayName("DateTimeOffset")]
    public class DateTimeOffsetProperty : WorkflowProperty
    {
        DateTimeOffset value;
        event Action<DateTimeOffset> ValueChanged;

        /// <summary>
        /// Gets or sets the value of the property.
        /// </summary>
        [XmlIgnore]
        public DateTimeOffset Value
        {
            get { return value; }
            set
            {
                this.value = value;
                OnValueChanged(value);
            }
        }

        /// <summary>
        /// Gets or sets an XML representation of the property value for serialization.
        /// </summary>
        [Browsable(false)]
        [XmlElement("Value")]
        public string ValueXml
        {
            get { return Value.ToString("o"); }
            set { Value = DateTimeOffset.Parse(value); }
        }

        void OnValueChanged(DateTimeOffset value)
        {
            var handler = ValueChanged;
            if (handler != null)
            {
                handler(value);
            }
        }

        /// <summary>
        /// Generates an observable sequence that produces a value whenever the
        /// workflow property changes, starting with the initial property value.
        /// </summary>
        /// <returns>An observable sequence of property values.</returns>
        public virtual IObservable<DateTimeOffset> Generate()
        {
            return Observable
                .Return(value)
                .Concat(Observable.FromEvent<DateTimeOffset>(
                    handler => ValueChanged += handler,
                    handler => ValueChanged -= handler));
        }
    }
}