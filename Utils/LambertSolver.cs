﻿//  Author:
//       Allis Tauri <allista@gmail.com>
//
//  Copyright (c) 2016 Allis Tauri
//
// This work is licensed under the Creative Commons Attribution-ShareAlike 4.0 International License. 
// To view a copy of this license, visit http://creativecommons.org/licenses/by-sa/4.0/ 
// or send a letter to Creative Commons, PO Box 1866, Mountain View, CA 94042, USA.
//
using System;
using UnityEngine;

namespace ThrottleControlledAvionics
{
	/// <summary>
	/// Lambert solver.
	/// Uses Sun's analytical solution to the Lambert problem with zero revolutions 
	/// and the Laguerre iterative root finder as described in:
	/// Wagner, Samuel Arthur, "Automated trajectory design for impulsive and low thrust interplanetary mission analysis" (2014). 
	/// Graduate Theses and Dissertations. Paper 14238.
	/// Because I can't get the fucking original paper by Sun anywhere =/
	/// </summary>
	public class LambertSolver
	{
		Orbit orbit;
		CelestialBody body;
		double StartUT;
		double mu; //standard grav. paramenter of the reference body

		Vector3d r1; //start position
		Vector3d c;  //r2-r1
		//magnitudes
		double   cm;
		double   m; //r1m+r2m+cm
		double   n; //r1m+r2m-cm
		//transfer parameters
		double   sigma; //angle parameter
		double   tau;   //normalized transfer time
		double   tauP;  //normalized parabolic transfer time
		double   tauME; //normalized Minimum Energy transfer time
		//utility
		double sigma2, sigma3, sigma5;
		double m3;

		public LambertSolver(Orbit orb, Vector3d destination, double UT)
		{
			orbit = orb;
			body = orbit.referenceBody;
			StartUT = UT;
			mu = body.gravParameter;

			r1 = orb.getRelativePositionAtUT(UT);
			var h  = Vector3d.Cross(r1, destination);
			if(h.sqrMagnitude < 0.01) h = orb.GetOrbitNormal();
			c  = destination-r1;

			cm  = c.magnitude;
			var r1m = r1.magnitude;
			var r2m = destination.magnitude;
			var rrm = r1m+r2m;
			m  = rrm+cm;
			n  = rrm-cm;
			m3 = m*m*m;

			var transfer_angle = Vector3d.Angle(r1, destination)*Mathf.Deg2Rad;
			if(h.z < 0) transfer_angle = Utils.TwoPI-transfer_angle;
			sigma = Math.Sqrt(n/m);
			if(transfer_angle > Math.PI) sigma = -sigma;
			sigma2 = sigma*sigma;
			sigma3 = sigma2*sigma;
			sigma5 = sigma2*sigma3;

			tauP = 2/3.0*(1-sigma3);
			tauME = Math.Acos(sigma)+sigma*Math.Sqrt(1-sigma2);

			Utils.LogF("\n" +
			           "r1: {}\n" +
			           "r2: {}\n" +
			           "c:  {}\n" +
			           "mu: {}\n" +
			           "sigma: {}\n" +
			           "tauME: {}\n",
			           r1, destination, c, mu,
			           sigma, tauME
			          );//debug
		}

		/// <summary>
		/// Calculates the ME transfer orbit from a given orbit and UT to the destination radius-vector.
		/// </summary>
		/// <returns>The DeltaVee for the maneuver.</returns>
		/// <param name="transfer_time">Returned value of the transfer time in seconds.</param>
		public Vector3d dV4TransferME(out double transfer_time)
		{
			var v = Math.Sqrt(mu)*Math.Sign(sigma)*Math.Sqrt(1-sigma2)/Math.Sqrt(n);
			transfer_time = invtau(tauME);
			return (r1.normalized + c/cm)*v - orbit.getOrbitalVelocityAtUT(StartUT);
		}

		/// <summary>
		/// Calculates the parabolic transfer orbit from a given orbit and UT to the destination radius-vector.
		/// </summary>
		/// <returns>The DeltaVee for the maneuver.</returns>
		/// <param name="transfer_time">Returned value of the transfer time in seconds.</param>
		public Vector3d dV4TransferP(out double transfer_time)
		{
			transfer_time = invtau(tauP);
			return dV(1, Math.Sign(sigma));
		}

		/// <summary>
		/// Calculates a transfer orbit from a given orbit and UT to the destination radius-vector in a given transfer time.
		/// </summary>
		/// <returns>The DeltaVee for the maneuver.</returns>
		/// <param name="transfer_time">Transfer time.</param>
		/// <param name="tol">Error tolerance.</param>
		public Vector3d dV4Transfer(double transfer_time, double tol = 1e-6)
		{
			tau = 4 * transfer_time * Math.Sqrt(mu/(m*m*m));
			Utils.LogF("tau: {}, tau/pi: {}, tauP {}", tau, tau/Math.PI, tauP);//debug
			if(Math.Abs(tau-tauME) < tol) return dV4TransferME(out transfer_time);
			if(tau <= tauP)
			{
				if(Math.Abs(tau-tauP) < tol) return dV4TransferP(out transfer_time);
				else //TODO: implement hyperbolic transfers
				{
					Utils.LogF("dV4Transfer: hyperbolic transfer orbits are not yet supported.");
					return Vector3d.zero;
				}
			}
			var N = 1;
			var x1 = double.NaN;
			while(double.IsNaN(x1) && N <= 1024)
			{
				var x0 = 0.0;
				if(double.IsNaN(x1))
					x1 = tau < tauME? 0.5 : -0.5;
				while(Math.Abs(x1-x0) > tol)
				{
					x0 = x1;
					x1 = next_elliptic(x1, N);
					if(double.IsNaN(x1)) break;
				}
				N *= 2;
			}
			if(double.IsNaN(x1))
			{
				Utils.Log("Unable to solve transfer orbit: {0}", transfer_time);
				return Vector3d.zero;
			}
			return dV(x1, _y(x1));
		}

		double _y(double x) { return Math.Sign(sigma)*Math.Sqrt(1-sigma2*(1-x*x)); }

		double invtau(double _tau) { return _tau/4/Math.Sqrt(mu/m3); }

		Vector3d dV(double x, double y)
		{
			var sqrt_mu = Math.Sqrt(mu);
			var sqrt_m  = Math.Sqrt(m);
			var sqrt_n  = Math.Sqrt(n);
			var vr = sqrt_mu * (y/sqrt_n - x/sqrt_m);
			var vc = sqrt_mu * (y/sqrt_n + x/sqrt_m);
			return r1.normalized*vr + c/cm*vc - orbit.getOrbitalVelocityAtUT(StartUT);
		}

		double next_elliptic(double x, int N)
		{
			var y = _y(x);
			var x2 = x*x;
			var x3 = x*x2;
			var y2 = y*y;
			var y3 = y*y2;
			var sqrt_one_x2 = Math.Sqrt(1 - x2);
			var sqrt_one_y2 = Math.Sqrt(1 - y2);

			var f = (((Math.Acos(x)-x*sqrt_one_x2) -
			          (Math.Atan(sqrt_one_y2/y)-y*sqrt_one_y2))
					 /(sqrt_one_x2 * sqrt_one_x2 * sqrt_one_x2) -tau);

			var f1 = (1/(1-x2) *
			          (3*x*(f+tau) - 2*(1-sigma3*x/Math.Abs(y))));
				
			var f2 = (1/(x-x3) *
			          ((1+4*x2)*f1 + 2*(1-sigma5*x3/Math.Abs(y3))));

			return Laguerre_next(x, f, f1, f2, N);
		}

		/// <summary>
		/// Computes the next approximation of the polynomial root using Laguerre method.
		/// The root is bounded in the [-1, 1] interval.
		/// </summary>
		/// <returns>Aapproximation of the root x of the polynomial f.</returns>
		/// <param name="x">The previous x approximation.</param>
		/// <param name="f">F(x).</param>
		/// <param name="f1">F'(x).</param>
		/// <param name="f2">F''(x).</param>
		/// <param name="N">Polynomial rank.</param>
		static double Laguerre_next(double x, double f, double f1, double f2, int N)
		{
			var G  = f1/f;
			var G2 = G*G;
			var H  = G2 - f2/f;
			var s2 = (N-1)*(N*H-G2);
			if(double.IsNaN(s2)) return double.NaN;
			var s  = Math.Sqrt(s2);
			var Gs = G+s;
			var G_s = G-s;
			var a = N/(Math.Abs(Gs) > Math.Abs(G_s)? Gs : G_s);
			while(Math.Abs(x-a) > 1) a /= 2;
			Utils.LogF("N {}, x0 {}, x1 {}, f {}, f1 {}, f2 {}, G {}, H {}, s {}, a {}", N, x, x-a, f, f1, f2, G, H, s, a);//debug
			return x - a;
		}
	}
}
