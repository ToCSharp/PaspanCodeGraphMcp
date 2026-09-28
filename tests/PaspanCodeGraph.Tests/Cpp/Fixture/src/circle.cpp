#include "geo/circle.h"

using namespace geo;

namespace {
constexpr double pi = 3.14159;
}

Circle::Circle(double radius) : Shape("circle"), radius_(radius) {}

Circle::Circle(const Point& center, double radius) : Circle(radius)
{
    origin_ = center;
}

double Circle::area() const
{
    return pi * radius_ * radius_;
}

double Circle::perimeter() const
{
    return 2 * pi * radius();
}

Circle Circle::unit()
{
    return Circle(1.0);
}

geo::Rect::Rect(double width, double height) : Shape("rect"), width_(width), height_(height) {}

double geo::Rect::area() const
{
    return width() * height();
}

double geo::Rect::width() const
{
    return width_;
}

double geo::Rect::height() const
{
    return height_;
}

geo::Rect::Corner geo::Rect::corner(int index) const
{
    Corner c{origin(), index};
    if (index % 2 == 1)
    {
        c.at.x += width_;
    }

    return c;
}

double Square::area() const
{
    return Rect::area();
}
