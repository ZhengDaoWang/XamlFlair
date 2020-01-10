﻿using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Numerics;
using System.Reactive.Linq;
using XamlFlair.Extensions;

#if __WPF__
using System.Windows;
using System.Windows.Media.Animation;
using static System.Windows.EventsMixin;
using FrameworkElement = System.Windows.FrameworkElement;
#else
using Windows.UI.Xaml;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Composition;
using static Windows.UI.Xaml.EventsMixin;
using FrameworkElement = Windows.UI.Xaml.FrameworkElement;
#endif

#if __WPF__
using Timeline = System.Windows.Media.Animation.Storyboard;
using XamlFlair.WPF.Logging;
#elif __UWP__
using Timeline = XamlFlair.AnimationGroup;
using XamlFlair.UWP.Logging;
#elif __UNO__
using Timeline = Windows.UI.Xaml.Media.Animation.Storyboard;
using XamlFlair.UnoPlatform.Logging;
#endif

namespace XamlFlair
{
	public static partial class Animations
	{
		private static readonly ConcurrentDictionary<Guid, ActiveTimeline<Timeline>> _actives = new ConcurrentDictionary<Guid, ActiveTimeline<Timeline>>();

		internal static readonly ILog Logger = LogProvider.GetCurrentClassLogger();

		internal static bool IsInDesignMode(DependencyObject d)
		{
#if __WPF__
			return System.ComponentModel.DesignerProperties.GetIsInDesignMode(d);
#elif __UWP__
			return Windows.ApplicationModel.DesignMode.DesignMode2Enabled;
#else
			return false;
#endif
		}

#region Attached Property Callbacks

		private static void OnPrimaryBindingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
		{
			HandleBindingChange(d, e, useSecondaryAnimation: false);
		}

		private static void OnSecondaryBindingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
		{
			HandleBindingChange(d, e, useSecondaryAnimation: true);
		}

		private static void OnPrimaryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
		{
			// Prevent running animations in a Visual Designer
			if (IsInDesignMode(d))
			{
				return;
			}

			if (d is FrameworkElement element)
			{
				InitializeElement(element);

				RegisterElementEvents(element, e.NewValue as IAnimationSettings);
			}
		}

		private static void OnSecondaryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
		{
			// Prevent running animations in a Visual Designer
			if (IsInDesignMode(d))
			{
				return;
			}

			if (d is FrameworkElement element)
			{
				InitializeElement(element);

				RegisterElementEvents(element, e.NewValue as IAnimationSettings, useSecondarySettings: true);
			}
		}

		private static void OnStartWithChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
		{
			// Prevent running animations in a Visual Designer
			if (IsInDesignMode(d))
			{
				return;
			}

			if (d is FrameworkElement element)
			{
				InitializeElement(element);
			}
		}

		// This can be called from the three main entry-points (Primary, Secondary, and StartWith)
		private static void InitializeElement(FrameworkElement element)
		{
			if (GetIsInitialized(element))
			{
				return;
			}

			// Set IsInitialized to true to only run this code once per element
			SetIsInitialized(element, true);

#if __UWP__
			// The new way of handling translate animations (see Translation property section):
			// https://blogs.windows.com/buildingapps/2017/06/22/sweet-ui-made-possible-easy-windows-ui-windows-10-creators-update/
			ElementCompositionPreview.SetIsTranslationEnabled(element, true);
#endif

			element
				.Events()
				.LoadedUntilUnloaded
				.Take(1)
				.Select(args => args.Sender as FrameworkElement)
				.Subscribe(
					elem =>
					{
						// Perform validations on element's attached properties
						Validate(elem);

						var startSettings = elem.GetSettings(SettingsTarget.StartWith, getStartWithFunc: GetStartWith);

						// If any StartWith settings were specified, apply them
						if (startSettings != null)
						{
							elem.ApplyInitialSettings((AnimationSettings)startSettings);
						}
					},
					ex => Logger.ErrorException($"Error on subscription to the {nameof(FrameworkElementEvents.LoadedUntilUnloaded)} event.", ex)
				);

			element
				.Events()
				.Unloaded
				.Subscribe(
					args => CleanupDisposables(args.Sender as FrameworkElement),
					ex => Logger.ErrorException($"Error on subscription to the {nameof(FrameworkElement.Unloaded)} event.", ex)
				);

			element
				.Observe(UIElement.VisibilityProperty)
				.TakeUntil(element.Events().Unloaded)
				.Subscribe(
					_ =>
					{
						var isVisible = element.Visibility == Visibility.Visible;
						var elementGuid = GetElementGuid(element);

						if (isVisible && _actives.GetNextIdleActiveTimeline(elementGuid)?.Timeline is Timeline idle)
						{
							RunNextAnimation(idle, element);
						}
					},
					ex => Logger.ErrorException($"Error on subscription to the {nameof(FrameworkElement.Visibility)} changes of {nameof(FrameworkElement)}", ex)
				);

#if __UWP__
			element
				.Events()
				.SizeChanged
				.TakeUntil(element.Events().Unloaded)
				.Subscribe(
					args =>
					{
						// If the element child is a SpriteVisual, maintain its size so update any effects applied
						if (args.Sender is FrameworkElement elem
							&& ElementCompositionPreview.GetElementChildVisual(elem) is SpriteVisual sprite)
						{
							sprite.Size = new Vector2((float)elem.ActualWidth, (float)elem.ActualHeight);
						}
					},
					ex => Logger.ErrorException($"Error on subscription to the {nameof(FrameworkElement.SizeChanged)} event.", ex)
				);
#endif
		}

#endregion

#region Events

		private static void RegisterElementEvents(FrameworkElement element, IAnimationSettings settings, bool useSecondarySettings = false)
		{
			switch (settings?.Event ?? AnimationSettings.DEFAULT_EVENT)
			{
				case EventType.Loaded:
					{
						element
							.Events()
							.LoadedUntilUnloaded
							.Subscribe(
								args => PrepareAnimations(args.Sender as FrameworkElement, useSecondaryAnimation: useSecondarySettings),
								ex => Logger.ErrorException($"Error on subscription to the {nameof(FrameworkElement.Loaded)} event.", ex),
								() => Cleanup(element)
							);

						break;
					}

				case EventType.Visibility:
					{
						element
							.Observe(FrameworkElement.VisibilityProperty)
							.Where(_ => element.Visibility == Visibility.Visible)
							.TakeUntil(element.Events().Unloaded)
							.Subscribe(
								_ => PrepareAnimations(element, useSecondaryAnimation: useSecondarySettings),
								ex => Logger.ErrorException($"Error on subscription to the {nameof(FrameworkElement.Visibility)} changes of {nameof(FrameworkElement)}", ex),
								() => Cleanup(element)
							);

						break;
					}

				case EventType.DataContextChanged:
					{
						element
							.Events()
							.DataContextChanged
							.DistinctUntilChanged(args => args.EventArgs.NewValue)
							.TakeUntil(element.Events().Unloaded)
							.Subscribe(
								args => PrepareAnimations(args.Sender as FrameworkElement, useSecondaryAnimation: useSecondarySettings),
								ex => Logger.ErrorException($"Error on subscription to the {nameof(FrameworkElement.DataContextChanged)} event.", ex),
								() => Cleanup(element)
							);

						break;
					}

				case EventType.PointerOver:
					{
						element
							.Events()
							.PointerEntered
							.TakeUntil(element.Events().Unloaded)
							.Subscribe(
								args => PrepareAnimations(args.Sender as FrameworkElement, useSecondaryAnimation: useSecondarySettings),
#if __WPF__
								ex => Logger.ErrorException($"Error on subscription to the {nameof(FrameworkElement.MouseEnter)} event.", ex),
#else
								ex => Logger.ErrorException($"Error on subscription to the {nameof(FrameworkElement.PointerEntered)} event.", ex),
#endif
								() => Cleanup(element)
							);

						break;
					}

				case EventType.PointerExit:
					{
						element
							.Events()
							.PointerExited
							.TakeUntil(element.Events().Unloaded)
							.Subscribe(
								args => PrepareAnimations(args.Sender as FrameworkElement, useSecondaryAnimation: useSecondarySettings),
#if __WPF__
								ex => Logger.ErrorException($"Error on subscription to the {nameof(FrameworkElement.MouseLeave)} event.", ex),
#else
								ex => Logger.ErrorException($"Error on subscription to the {nameof(FrameworkElement.PointerExited)} event.", ex),
#endif
								() => Cleanup(element)
							);

						break;
					}

				case EventType.GotFocus:
					{
						element
							.Events()
							.GotFocus
							.TakeUntil(element.Events().Unloaded)
							.Subscribe(
								args => PrepareAnimations(args.Sender as FrameworkElement, useSecondaryAnimation: useSecondarySettings),
								ex => Logger.ErrorException($"Error on subscription to the {nameof(FrameworkElement.GotFocus)} event.", ex),
								() => Cleanup(element)
							);

						break;
					}

				case EventType.LostFocus:
					{
						element
							.Events()
							.LostFocus
							.TakeUntil(element.Events().Unloaded)
							.Subscribe(
								args => PrepareAnimations(args.Sender as FrameworkElement, useSecondaryAnimation: useSecondarySettings),
								ex => Logger.ErrorException($"Error on subscription to the {nameof(FrameworkElement.LostFocus)} event.", ex),
								() => Cleanup(element)
							);

						break;
					}
			}
		}

		private static void Timeline_Completed(object sender, object e)
		{
#if __WPF__
			var timeline = (sender as ClockGroup)?.Timeline as Timeline;
#else
			var timeline = sender as Timeline;
#endif
			// Unregister the Completed event
			UnregisterTimeline(timeline);

			if (_actives.GetElementByTimeline(timeline) is FrameworkElement element)
			{
				RunNextAnimation(timeline, element);
			}
		}

		private static void RunNextAnimation(Timeline timeline, FrameworkElement element)
		{
			var timelineGuid = GetTimelineGuid(timeline);
			var elementGuid = GetElementGuid(element);
			var active = _actives.FindActiveTimeline(timelineGuid);

			active.SetAnimationState(timelineGuid, AnimationState.Completed);

			// If an idle animation exists, run it
			if (_actives.GetNextIdleActiveTimeline(elementGuid)?.Settings is AnimationSettings idleSettings)
			{
				RunAnimation(element, idleSettings, runFromIdle: true);
			}

			// Else if the animation is a repetitive sequence and they're all completed,
			// then reset the Completed to be Idle and re-start the sequence
			else if (active.IsSequence && active.IsIterating && _actives.AllIteratingCompleted(elementGuid))
			{
				_actives.ResetAllIteratingCompletedToIdle(elementGuid);

				// Make sure to run the next iteration on visible elements only
				if (element.Visibility == Visibility.Visible)
				{
					var first = _actives.FindFirstActiveTimeline(elementGuid);
					RunAnimation(element, first.Settings, runFromIdle: true);
				}
			}

			// Else if the animation needs to repeat, re-start it
			else if (!active.IsSequence && active.IsIterating)
			{
				active.SetAnimationState(timelineGuid, AnimationState.Idle);

				// Make sure to run the next iteration on visible elements only
				if (element.Visibility == Visibility.Visible)
				{
					RunAnimation(element, active.Settings, runFromIdle: true);
				}
			}

			// Else if it's done animating, clean it up
			else if (active.IterationCount <= 1 && active.IterationBehavior != IterationBehavior.Forever)
			{
				Cleanup(elementGuid, stopAnimation: false);
			}
		}

#endregion

#region Methods

		private static void HandleBindingChange(DependencyObject d, DependencyPropertyChangedEventArgs e, bool useSecondaryAnimation)
		{
			// Prevent running animations in a Visual Designer
			if (IsInDesignMode(d))
			{
				return;
			}

			if (d is FrameworkElement element && e.NewValue is bool isAnimating && isAnimating)
			{
				PrepareAnimations(element, useSecondaryAnimation);
			}
		}

		internal static void RunAnimation(FrameworkElement element, AnimationSettings settings, bool isSequence = false)
		{
			RunAnimation(element, settings, runFromIdle: false, isSequence: isSequence);
		}

		private static void RunAnimation(FrameworkElement element, AnimationSettings settings, bool runFromIdle, bool isSequence = false)
		{
			var timeline = new Timeline();
			var iterationBehavior = GetIterationBehavior(element);
			var iterationCount = GetIterationCount(element);

			// FADE IN/OUT
			if (settings.Kind.HasFlag(AnimationKind.FadeTo))
			{
				element.FadeTo(settings, ref timeline);
			}
			else if (settings.Kind.HasFlag(AnimationKind.FadeFrom))
			{
				element.FadeFrom(settings, ref timeline);
			}

			// ROTATE TO/FROM
			if (settings.Kind.HasFlag(AnimationKind.RotateTo))
			{
				element.RotateTo(settings, ref timeline);
			}
			else if (settings.Kind.HasFlag(AnimationKind.RotateFrom))
			{
				element.RotateFrom(settings, ref timeline);
			}

			// SCALE TO/FROM
			if (settings.Kind.HasFlag(AnimationKind.ScaleXTo))
			{
				element.ScaleXTo(settings, ref timeline);
			}
			else if (settings.Kind.HasFlag(AnimationKind.ScaleXFrom))
			{
				element.ScaleXFrom(settings, ref timeline);
			}
			if (settings.Kind.HasFlag(AnimationKind.ScaleYTo))
			{
				element.ScaleYTo(settings, ref timeline);
			}
			else if (settings.Kind.HasFlag(AnimationKind.ScaleYFrom))
			{
				element.ScaleYFrom(settings, ref timeline);
			}
#if __UWP__
			if (settings.Kind.HasFlag(AnimationKind.ScaleZTo))
			{
				element.ScaleZTo(settings, ref timeline);
			}
			else if (settings.Kind.HasFlag(AnimationKind.ScaleZFrom))
			{
				element.ScaleZFrom(settings, ref timeline);
			}
#endif

			// TRANSLATE TO/FROM
			if (settings.Kind.HasFlag(AnimationKind.TranslateXTo))
			{
				element.TranslateXTo(settings, ref timeline);
			}
			else if (settings.Kind.HasFlag(AnimationKind.TranslateXFrom))
			{
				element.TranslateXFrom(settings, ref timeline);
			}
			if (settings.Kind.HasFlag(AnimationKind.TranslateYTo))
			{
				element.TranslateYTo(settings, ref timeline);
			}
			else if (settings.Kind.HasFlag(AnimationKind.TranslateYFrom))
			{
				element.TranslateYFrom(settings, ref timeline);
			}
#if __UWP__
			if (settings.Kind.HasFlag(AnimationKind.TranslateZTo))
			{
				element.TranslateZTo(settings, ref timeline);
			}
			else if (settings.Kind.HasFlag(AnimationKind.TranslateZFrom))
			{
				element.TranslateZFrom(settings, ref timeline);
			}
#endif

#if __WPF__ || __UWP__
			// BLUR TO/FROM
			if (settings.Kind.HasFlag(AnimationKind.BlurTo))
			{
				element.BlurTo(settings, ref timeline);
			}
			else if (settings.Kind.HasFlag(AnimationKind.BlurFrom))
			{
				element.BlurFrom(settings, ref timeline);
			}
#endif

#if __UWP__
			// SATURATE TO/FROM
			if (settings.Kind.HasFlag(AnimationKind.SaturateTo))
			{
				element.SaturateTo(settings, ref timeline);
			}
			else if (settings.Kind.HasFlag(AnimationKind.SaturateFrom))
			{
				element.SaturateFrom(settings, ref timeline);
			}

			// TINT TO/FROM
			if (settings.Kind.HasFlag(AnimationKind.TintTo))
			{
				element.TintTo(settings, ref timeline);
			}
			else if (settings.Kind.HasFlag(AnimationKind.TintFrom))
			{
				element.TintFrom(settings, ref timeline);
			}
#endif

			ActiveTimeline<Timeline> active = null;

			if (runFromIdle)
			{
				// If the animation is running for an "idle" ActiveTimeline,
				// then it must be set to the existing ActiveTimeline
				// instead of creating a new one
				var guid = GetElementGuid(element);
				active = _actives.SetTimeline(guid, timeline);
			}
			else
			{
				// Add the new ActiveTimeline
				active = _actives.Add(timeline, settings, element, AnimationState.Idle, iterationBehavior, iterationCount, isSequence);
			}

			// We decrement the iteration count right before running the animation
			if (active.IterationCount > 0)
			{
				active.IterationCount--;
			}

			StartTimeline(timeline);
		}

		private static void StartTimeline(Timeline timeline)
		{
			timeline.Completed += Timeline_Completed;
			timeline.Begin();

			_actives.SetAnimationState(GetTimelineGuid(timeline), AnimationState.Running);
		}

		private static void PrepareAnimations(FrameworkElement element, bool useSecondaryAnimation = false)
		{
			if (element == null)
			{
				return;
			}

			// Make sure to not start an animation when an element is not visible
			if (element.Visibility != Visibility.Visible)
			{
				return;
			}

			// Make sure to stop any running animations
			if (_actives.IsElementAnimating(GetElementGuid(element)))
			{
				foreach (var active in _actives.GetAllNonIteratingActiveTimelines(GetElementGuid(element)))
				{
					active.Timeline.Stop();
				}
			}

			var animationSettings = element.GetSettings(
				useSecondaryAnimation ? SettingsTarget.Secondary : SettingsTarget.Primary,
				getPrimaryFunc: GetPrimary,
				getSecondaryFunc: GetSecondary);

			// Settings can be null if a Trigger is set before the associated element is loaded
			if (animationSettings == null)
			{
				return;
			}

			var settingsList = animationSettings.ToSettingsList();
			var startFirst = true;
			var iterationBehavior = GetIterationBehavior(element);
			var iterationCount = GetIterationCount(element);
			var sequenceCounter = 0;

			foreach (var settings in settingsList)
			{
				var isSequence = settingsList.Count > 1;

				// The "first" animation must always run immediately
				if (startFirst)
				{
					RunAnimation(element, settings, isSequence);

					startFirst = false;
				}
				else
				{
					_actives.Add(null, settings, element, AnimationState.Idle, iterationBehavior, iterationCount, isSequence, sequenceOrder: sequenceCounter);
				}

				sequenceCounter++;
			}
		}

		private static void UnregisterTimeline(Timeline timeline)
		{
			if (timeline == null)
			{
				return;
			}
#if __WPF__
			var timelineGuid = GetTimelineGuid(timeline);

			// Retrieve the original Storyboard since the one passed in is
			// Frozen (to be able to unsubscribe from the event)
			var original = _actives.FindActiveTimeline(timelineGuid)?.Timeline;

			if (original != null)
			{
				original.Completed -= Timeline_Completed;
			}
#else
			timeline.Completed -= Timeline_Completed;
#endif
		}

		private static void Cleanup(FrameworkElement element, bool includeIterating = true, bool stopAnimation = true)
		{
			Cleanup(GetElementGuid(element), stopAnimation);
		}

		private static void Cleanup(Guid elementGuid, bool stopAnimation = true)
		{
			var result = _actives.GetAllKeyValuePairs(elementGuid);

			foreach (var kvp in result.ToArray())
			{
				var timeline = kvp.Value.Timeline;

				if (timeline != null)
				{
					// We should only stop when the control unloads (since it can cause values to reset)
					if (stopAnimation)
					{
						timeline?.Stop();
					}

					UnregisterTimeline(timeline);
					CleanupTimeline(timeline);
				}

				_actives.RemoveByID(kvp.Key);
			}
		}

		private static void CleanupTimeline(Timeline timeline)
		{
			if (timeline == null)
			{
				return;
			}

			var timelineGuid = GetTimelineGuid(timeline);
#if __WPF__
			// Retrieve the original Storyboard since the one passed in is
			// Frozen (to be able to unsubscribe from the event)
			var original = _actives.FindActiveTimeline(timelineGuid)?.Timeline;

			if (original != null)
			{
				original = null;
			}
#elif __UWP__
			timeline.Cleanup();
#endif
			timeline = null;
		}

		private static void CleanupDisposables(FrameworkElement element)
		{
			var disposables = GetDisposables(element);
			disposables?.Dispose();
			disposables = null;
#if __UWP__
			var sprite = GetSprite(element);
			sprite?.Dispose();
			sprite = null;
#endif
		}

#endregion
	}
}