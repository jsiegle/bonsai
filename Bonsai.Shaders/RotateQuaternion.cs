﻿using OpenTK;
using System.ComponentModel;

namespace Bonsai.Shaders
{
    [Description("Applies a rotation transformation specified by a quaternion.")]
    public class RotateQuaternion : MatrixTransform
    {
        Quaternion rotation;

        public RotateQuaternion()
        {
            rotation = Quaternion.Identity;
        }

        [TypeConverter(typeof(NumericRecordConverter))]
        [Description("The quaternion representing the rotation transformation.")]
        public Quaternion Rotation
        {
            get { return rotation; }
            set { rotation = value; }
        }

        protected override void CreateTransform(out Matrix4 result)
        {
            Matrix4.CreateFromQuaternion(ref rotation, out result);
        }
    }
}
