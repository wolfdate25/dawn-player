using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DawnPlayer.App.ViewModels;

/// <summary>
/// Base class for all presentation ViewModels implementing <see cref="INotifyPropertyChanged"/>
/// with value-equality checks and clamping helpers to prevent UI feedback loops.
/// </summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
        {
            return false;
        }

        storage = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected bool SetClampedProperty(ref double storage, double value, double min, double max, double epsilon = 1e-4, [CallerMemberName] string? propertyName = null)
    {
        double clamped = Math.Clamp(value, min, max);
        if (Math.Abs(storage - clamped) < epsilon)
        {
            return false;
        }

        storage = clamped;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected bool SetClampedProperty(ref int storage, int value, int min, int max, [CallerMemberName] string? propertyName = null)
    {
        int clamped = Math.Clamp(value, min, max);
        if (storage == clamped)
        {
            return false;
        }

        storage = clamped;
        OnPropertyChanged(propertyName);
        return true;
    }
}
