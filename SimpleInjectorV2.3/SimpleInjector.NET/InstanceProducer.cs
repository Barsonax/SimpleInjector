﻿#region Copyright (c) 2010 S. van Deursen
/* The Simple Injector is an easy-to-use Inversion of Control library for .NET
 * 
 * Copyright (C) 2010 S. van Deursen
 * 
 * To contact me, please visit my blog at http://www.cuttingedge.it/blogs/steven/ or mail to steven at 
 * cuttingedge.it.
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
 * associated documentation files (the "Software"), to deal in the Software without restriction, including 
 * without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell 
 * copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the 
 * following conditions:
 * 
 * The above copyright notice and this permission notice shall be included in all copies or substantial 
 * portions of the Software.
 * 
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT 
 * LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO 
 * EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER 
 * IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE 
 * USE OR OTHER DEALINGS IN THE SOFTWARE.
*/
#endregion

namespace SimpleInjector
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
    using System.Linq.Expressions;
    using SimpleInjector.Advanced;

    /// <summary>
    /// Produces instances for a given registration. Instances of this type are generally created by the
    /// container when calling one of the <b>Register</b> overloads. Instances can be retrieved by calling
    /// <see cref="Container.GetCurrentRegistrations"/> or <see cref="Container.GetRegistration(Type, bool)"/>.
    /// </summary>
    /// <remarks>
    /// The <b>Register</b> method overloads create <b>InstanceProducer</b> instances internally, but
    /// <b>InstanceProducer</b>s can be created manually to implement special scenarios. An 
    /// <b>InstanceProducer</b> wraps <see cref="Registration"/> instance. The <b>Registration</b> builds an
    /// <see cref="Expression"/> that describes the intend to create the instance according to a certain
    /// lifestyle. The <b>InstanceProducer</b> on the other hand transforms this <b>Expression</b> to a
    /// delegate and allows the actual instance to be created. A <b>Registration</b> itself can't create any
    /// instance. The <b>InsanceProducer</b> allows intercepting created instances by hooking onto the
    /// <see cref="SimpleInjector.Container.ExpressionBuilt">Container.ExpressionBuilt</see> event. The
    /// <see cref="SimpleInjector.Extensions.DecoratorExtensions.RegisterDecorator(Container, Type, Type)">RegisterDecorator</see>
    /// extension methods for instance work by hooking onto the <b>ExpressionBuilt</b> event and allow
    /// wrapping the returned instance with a decorator.
    /// </remarks>
    /// <example>
    /// The following example shows the creation of two different <b>InstanceProducer</b> instances that wrap
    /// the same <b>Registration</b> instance. Since the <b>Registration</b> is created using the 
    /// <see cref="SimpleInjector.Lifestyle.Singleton">Singleton</see> lifestyle, both producers will return 
    /// the same instance. The <b>InstanceProducer</b> for the <code>Interface1</code> however, will wrap that
    /// instance in a (transient) <code>Interface1Decorator</code>.
    /// <code lang="cs"><![CDATA[
    /// var container = new Container();
    /// 
    /// // ServiceImpl implements both Interface1 and Interface2.
    /// var registration = Lifestyle.Singleton.CreateRegistration<ServiceImpl, ServiceImpl>(container);
    /// 
    /// var producer1 = new InstanceProducer(typeof(Interface1), registration);
    /// var producer2 = new InstanceProducer(typeof(Interface2), registration);
    /// 
    /// container.RegisterDecorator(typeof(Interface1), typeof(Interface1Decorator));
    /// 
    /// var instance1 = (Interface1)producer1.GetInstance();
    /// var instance2 = (Interface2)producer2.GetInstance();
    /// 
    /// Assert.IsInstanceOfType(instance1, typeof(Interface1Decorator));
    /// Assert.IsInstanceOfType(instance2, typeof(ServiceImpl));
    /// 
    /// Assert.AreSame(((Interface1Decorator)instance1).DecoratedInstance, instance2);
    /// ]]></code>
    /// </example>
    [DebuggerTypeProxy(typeof(InstanceProducerDebugView))]
    [DebuggerDisplay(
        "ServiceType = {SimpleInjector.Helpers.ToFriendlyName(ServiceType),nq}, " +
        "Lifestyle = {Lifestyle.Name,nq}")]
    public sealed class InstanceProducer
    {
        private static readonly Action[] NoVerifiers = new Action[0];

        private readonly object locker = new object();

        private CyclicDependencyValidator validator;
        private Func<object> instanceCreator;
        private Lazy<Expression> expression;
        private bool? isValid = true;
        private Lifestyle overriddenLifestyle;
        private KnownRelationship[] relationships;
        private List<Action> verifiers;

        /// <summary>Initializes a new instance of the <see cref="InstanceProducer"/> class.</summary>
        /// <param name="serviceType">The service type for which this instance is created.</param>
        /// <param name="registration">The <see cref="Registration"/>.</param>
        public InstanceProducer(Type serviceType, Registration registration)
        {
            Requires.IsNotNull(serviceType, "serviceType");
            Requires.IsNotNull(registration, "registration");

            this.ServiceType = serviceType;
            this.Registration = registration;

            this.validator = new CyclicDependencyValidator(registration.ImplementationType);

            this.expression = new Lazy<Expression>(this.BuildExpressionInternal);
        }

        /// <summary>
        /// Gets the <see cref="Lifestyle"/> for this registration. The returned lifestyle can differ from the
        /// lifestyle that is used during the registration. This can happen for instance when the registration
        /// is changed by an <see cref="Container.ExpressionBuilt">ExpressionBuilt</see> registration or
        /// gets <see cref="SimpleInjector.Extensions.DecoratorExtensions">decorated</see>.
        /// </summary>
        /// <value>The <see cref="Lifestyle"/> for this registration.</value>
        public Lifestyle Lifestyle
        {
            get { return this.overriddenLifestyle ?? this.Registration.Lifestyle; }
        }

        /// <summary>Gets the service type for which this producer produces instances.</summary>
        /// <value>A <see cref="Type"/> instance.</value>
        public Type ServiceType { get; private set; }

        /// <summary>
        /// Gets the <see cref="Registration"/> instance for this instance.
        /// </summary>
        /// <value>The <see cref="Registration"/>.</value>
        public Registration Registration { get; private set; }

        internal Type ImplementationType
        {
            get { return this.Registration.ImplementationType ?? this.ServiceType; }
        }

        // Flag that indicates that this type is created by the container (concrete or collection) or resolved
        // using unregistered type resolution.
        internal bool IsContainerAutoRegistered { get; set; }

        // Will only return false when the type is a concrete unregistered type that was automatically added
        // by the container, while the expression can not be generated.
        // Types that are registered upfront are always considered to be valid, while unregistered types must
        // be validated. The reason for this is that we must prevent the container to throw an exception when
        // GetRegistration() is called for an unregistered (concrete) type that can not be resolved.
        internal bool IsValid
        {
            get
            {
                if (this.isValid == null)
                {
                    this.isValid = this.CanBuildExpression();
                }

                return this.isValid.GetValueOrDefault();
            }
        }

        /// <summary>Produces an instance.</summary>
        /// <returns>An instance. Will never return null.</returns>
        /// <exception cref="ActivationException">When the instance could not be retrieved or is null.</exception>
        [SuppressMessage("Microsoft.Design", "CA1024:UsePropertiesWhereAppropriate", Justification =
            "A property is not appropriate, because get instance could possibly be a heavy operation.")]
        public object GetInstance()
        {
            this.validator.CheckForRecursiveCalls();

            object instance;

            try
            {
                if (this.instanceCreator == null)
                {
                    this.instanceCreator = this.BuildInstanceCreator(out instance);

                    if (instance == null)
                    {
                        instance = this.instanceCreator();
                    }
                }
                else
                {
                    instance = this.instanceCreator();
                }

                this.RemoveValidator();
            }
            catch (Exception ex)
            {
                this.validator.Reset();

                throw this.GetErrorForTryingToGetInstanceOfType(ex);
            }

            if (instance == null)
            {
                throw new ActivationException(StringResources.DelegateForTypeReturnedNull(this.ServiceType));
            }

            return instance;
        }

        /// <summary>
        /// Builds an expression that expresses the intent to get an instance by the current producer. A call 
        /// to this method locks the container. No new registrations can't be made after a call to this method.
        /// </summary>
        /// <returns>An Expression.</returns>
        public Expression BuildExpression()
        {
            this.validator.CheckForRecursiveCalls();

            try
            {
                var expression = this.expression.Value;

                this.RemoveValidator();

                return expression;
            }
            catch (Exception ex)
            {
                this.validator.Reset();

                throw this.GetErrorForTryingToGetInstanceOfType(ex);
            }
        }

        // Throws an InvalidOperationException on failure.
        internal object Verify()
        {
            object instance;

            try
            {
                // Test the creator
                // NOTE: We've got our first quirk in the design here: The returned object could implement
                // IDisposable, but there is no way for us to know if we should actually dispose this 
                // instance or not :-(. Disposing it could make us prevent a singleton from ever being
                // used; not disposing it could make us leak resources :-(.
                instance = this.GetInstance();

                this.DoExtraVerfication();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(StringResources.ConfigurationInvalidCreatingInstanceFailed(
                    this.ServiceType, ex), ex);
            }

            return instance;
        }

        internal void AddVerifier(Action action)
        {
            lock (this.locker)
            {
                var verifiers = this.verifiers ?? (this.verifiers = new List<Action>());

                verifiers.Add(action);
            }
        }

        internal KnownRelationship[] GetRelationships()
        {
            return this.relationships ?? this.Registration.GetRelationships();
        }

        internal void ReplaceRelationships(IEnumerable<KnownRelationship> relationships)
        {
            this.relationships = relationships.Distinct().ToArray();
        }

        internal void EnsureTypeWillBeExplicitlyVerified()
        {
            this.isValid = null;
        }

        private void DoExtraVerfication()
        {
            foreach (var verify in this.GetVerifiers())
            {
                verify();
            }
        }

        private Action[] GetVerifiers()
        {
            lock (this.locker)
            {
                return this.verifiers != null ? this.verifiers.ToArray() : NoVerifiers;
            }
        }

        private Func<object> BuildInstanceCreator(out object createdInstance)
        {
            // Don't do recursive checks. The GetInstance() already does that.
            var expression = this.expression.Value;

            try
            {
                return Helpers.CompileAndRun(this.Registration.Container, expression, out createdInstance);
            }
            catch (Exception ex)
            {
                string message =
                    StringResources.ErrorWhileBuildingDelegateFromExpression(this.ServiceType, expression, ex);

                throw new ActivationException(message, ex);
            }
        }

        private Expression BuildExpressionInternal()
        {
            // We must lock the container, because not locking could lead to race conditions.
            this.Registration.Container.LockContainer();

            var expression = this.Registration.BuildExpression();

            if (expression == null)
            {
                throw new ActivationException(StringResources.RegistrationReturnedNullFromBuildExpression(
                    this.Registration));
            }

            var e = new ExpressionBuiltEventArgs(this.ServiceType, expression);

            e.Lifestyle = this.Lifestyle;
            e.InstanceProducer = this;

            this.Registration.Container.OnExpressionBuilt(e, this);

            this.overriddenLifestyle = e.Lifestyle;

            return e.Expression;
        }

        private ActivationException GetErrorForTryingToGetInstanceOfType(Exception innerException)
        {
            string exceptionMessage = StringResources.DelegateForTypeThrewAnException(this.ServiceType);

            return new ActivationException(exceptionMessage + " " + innerException.Message, innerException);
        }

        // This method will be inlined by the JIT.
        private void RemoveValidator()
        {
            // No recursive calls detected, we can remove the validator to increase performance.
            // We first check for null, because this is faster. Every time we write, the CPU has to send
            // the new value to all the other CPUs. We only nullify the validator while using the GetInstance
            // method, because the BuildExpression will only be called a limited amount of time.
            if (this.validator != null)
            {
                this.validator = null;
            }
        }

        private bool CanBuildExpression()
        {
            try
            {
                // Test if the instance can be made.
                this.BuildExpression();

                return true;
            }
            catch (ActivationException)
            {
                return false;
            }
        }

        internal sealed class InstanceProducerDebugView
        {
            private readonly InstanceProducer instanceProducer;

            internal InstanceProducerDebugView(InstanceProducer instanceProducer)
            {
                this.instanceProducer = instanceProducer;
            }

            public Lifestyle Lifestyle
            {
                get { return this.instanceProducer.Lifestyle; }
            }

            [DebuggerDisplay("{SimpleInjector.Helpers.ToFriendlyName(ServiceType),nq}")]
            public Type ServiceType
            {
                get { return this.instanceProducer.ServiceType; }
            }

            [DebuggerDisplay("{SimpleInjector.Helpers.ToFriendlyName(ImplementationType),nq}")]
            public Type ImplementationType
            {
                get { return this.instanceProducer.ImplementationType; }
            }

            public KnownRelationship[] Relationships
            {
                get { return this.instanceProducer.Registration.GetRelationships(); }
            }
        }
    }
}